using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ServiceEngine.Core;
using ServiceEngine.IPC;

namespace ServiceEngine.Workers;

/// <summary>
/// Zero-polling foreground window watcher using SetWinEventHook.
/// Tracks active windows, calculates durations, detects browser tab changes,
/// and feeds the StateManager for enforcement decisions.
/// </summary>
public sealed class WindowWatcher : BackgroundService
{
    private readonly StateManager _state;
    private readonly ScreenTimeLogger _db;
    private readonly PipeServer _pipe;
    private readonly ILogger<WindowWatcher> _log;

    private string? _currentApp;
    private string? _currentTitle;
    private string? _currentCategory;
    private DateTime _focusStart;
    private string _currentSessionId = Guid.NewGuid().ToString();
    private readonly object _stateLock = new();

    private IntPtr _foregroundHook;
    private IntPtr _nameChangeHook;
    private WinEventDelegate? _delegateRef; // Prevent GC

    public WindowWatcher(
        StateManager state,
        ScreenTimeLogger db,
        PipeServer pipe,
        ILogger<WindowWatcher> log)
    {
        _state = state;
        _db = db;
        _pipe = pipe;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("WindowWatcher starting.");

        _delegateRef = OnWinEvent;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _delegateRef,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _nameChangeHook = SetWinEventHook(
            EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _delegateRef,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_foregroundHook == IntPtr.Zero)
            _log.LogError("Failed to install foreground hook. Error: {E}", Marshal.GetLastWin32Error());

        // Run a Win32 message pump so the hooks fire
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                await Task.Delay(50, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            if (_nameChangeHook != IntPtr.Zero) UnhookWinEvent(_nameChangeHook);
            _log.LogInformation("WindowWatcher stopped.");
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;

        string? appName = GetProcessName(hwnd);
        string? title = GetWindowTitle(hwnd);

        if (string.IsNullOrEmpty(appName)) return;

        // For EVENT_OBJECT_NAMECHANGE, only care about browser tabs (title changes in foreground)
        if (eventType == EVENT_OBJECT_NAMECHANGE)
        {
            if (appName != _currentApp) return; // Not foreground
            if (title == _currentTitle) return;  // No real change
        }

        // Flush time for the previous window
        string? prevApp, prevTitle, prevCategory;
        lock (_stateLock)
        {
            prevApp = _currentApp;
            prevTitle = _currentTitle;
            prevCategory = _currentCategory;
        }

        if (prevApp != null)
        {
            int elapsed = (int)(DateTime.Now - _focusStart).TotalSeconds;
            if (elapsed > 0)
            {
                _db.LogWindow(prevApp, prevTitle, prevCategory, elapsed, _currentSessionId);

                // Update budget used time asynchronously
                if (prevCategory != null)
                    SafeTask.Run(() => _db.AddUsedSecondsAsync(prevCategory, elapsed), _log, "WindowWatcher_AddUsedSeconds");
            }
        }

        // Detect pickup (context switch from productive to distracting)
        SafeTask.Run(async () =>
        {
            string? newCategory = null;
            var domain = ExtractDomain(title);
            if (domain != null) newCategory = await _db.GetCategoryForDomainAsync(domain);
            newCategory ??= await _db.GetCategoryForTitleAsync(title ?? "");
            newCategory ??= await _db.GetCategoryForAppAsync(appName);

            if (prevCategory is "productive" or "neutral" && newCategory == "distracting")
                await _db.LogPickupAsync(prevApp, appName, newCategory);

            lock (_stateLock)
            {
                _currentCategory = newCategory;
            }

            // Evaluate the new window for enforcement
            await EvaluateAndEnforceAsync(appName, title, domain);
        }, _log, "WindowWatcher_EvaluateNewFocus");

        lock (_stateLock)
        {
            _currentApp = appName;
            _currentTitle = title;
        }
        _focusStart = DateTime.Now;
        _currentSessionId = Guid.NewGuid().ToString();
    }

    private async Task EvaluateAndEnforceAsync(string appName, string? title, string? domain)
    {
        try
        {
            var decision = await _state.EvaluateAsync(appName, title, domain);
            _log.LogInformation("{App}: {Action} – {Reason}", appName, decision.Action, decision.Reason);

            switch (decision.Action)
            {
                case EnforcementAction.Kill:
                    SendCtrlW(appName);
                    KillProcess(appName);
                    break;

                case EnforcementAction.Friction:
                    await _pipe.BroadcastAsync(PipeMessage.ShowFriction(
                        appName, decision.Category ?? "distracting", decision.FrictionSeconds));
                    // Temporarily suppress the window during the pause
                    PauseWindow();
                    break;

                case EnforcementAction.Allow:
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error evaluating {App}", appName);
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static string? ExtractDomain(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        // Browser titles often end with " - Site Name" or contain the URL
        // Simple heuristic: look for known TLD patterns
        var parts = title.Split([' ', '-', '|'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Contains('.') && part.Length > 4 && !part.StartsWith('.'))
            {
                var candidate = part.ToLowerInvariant().TrimEnd('/');
                if (Uri.CheckHostName(candidate) == UriHostNameType.Dns)
                    return candidate;
            }
        }
        return null;
    }

    private static void SendCtrlW(string appName)
    {
        try
        {
            // Use keybd_event to properly send Ctrl+W key combo (closes browser tab)
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_W, 0, 0, UIntPtr.Zero);
            keybd_event(VK_W, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { /* best effort */ }
    }

    private static void KillProcess(string appName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(appName)))
            {
                try { p.Kill(); } catch { }
            }
        }
        catch { }
    }

    private static void PauseWindow()
    {
        // Minimize and disable interaction during friction delay
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_MINIMIZE);
    }

    private static string? GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return null; }
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return null;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ── Win32 P/Invoke ────────────────────────────────────────────────────────

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint PM_REMOVE = 0x0001;
    private const int SW_MINIMIZE = 6;
    private const int WM_KEYDOWN = 0x0100;
    private const byte VK_W = 0x57;
    private const byte VK_CONTROL = 0x11;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")] private static extern int GetWindowText(
        IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")] private static extern bool PostMessage(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern void keybd_event(
        byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")] private static extern bool PeekMessage(
        out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
