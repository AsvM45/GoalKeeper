using System.Management;
using ServiceEngine.Core;
using ServiceEngine.IPC;

namespace ServiceEngine.Workers;

/// <summary>
/// Subscribes to WMI __InstanceCreationEvent to intercept new process launches
/// before they fully initialize. Fires StateManager evaluation immediately.
/// </summary>
public sealed class ProcessWatcher : BackgroundService
{
    private readonly StateManager _state;
    private readonly PipeServer _pipe;
    private readonly ILogger<ProcessWatcher> _log;

    private ManagementEventWatcher? _watcher;

    public ProcessWatcher(StateManager state, PipeServer pipe, ILogger<ProcessWatcher> log)
    {
        _state = state;
        _pipe = pipe;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ProcessWatcher starting WMI subscription.");
        try
        {
            var query = new WqlEventQuery(
                "__InstanceCreationEvent",
                TimeSpan.FromSeconds(1),
                "TargetInstance ISA 'Win32_Process'");

            _watcher = new ManagementEventWatcher(query);
            _watcher.EventArrived += OnProcessCreated;
            _watcher.Start();

            _log.LogInformation("WMI process creation watcher active.");
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "ProcessWatcher WMI subscription failed.");
        }
        finally
        {
            _watcher?.Stop();
            _watcher?.Dispose();
            _log.LogInformation("ProcessWatcher stopped.");
        }
    }

    private void OnProcessCreated(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var proc = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var processName = proc["Name"]?.ToString() ?? "";
            var executablePath = proc["ExecutablePath"]?.ToString() ?? "";
            var commandLine = proc["CommandLine"]?.ToString() ?? "";

            _log.LogInformation("New process: {Name}", processName);

            // Evaluate on thread pool to avoid blocking WMI callbacks
            SafeTask.Run(async () =>
            {
                var decision = await _state.EvaluateAsync(processName, commandLine, null);
                _log.LogInformation("Decision for {Name}: {Action}", processName, decision.Action);

                switch (decision.Action)
                {
                    case EnforcementAction.Kill:
                        _log.LogInformation("Killing {Name}: {Reason}", processName, decision.Reason);
                        KillByName(processName);
                        break;

                    case EnforcementAction.Friction:
                        _log.LogInformation("Friction for {Name}", processName);
                        await _pipe.BroadcastAsync(PipeMessage.ShowFriction(
                            processName, decision.Category ?? "distracting", decision.FrictionSeconds));
                        // Suspend the new process during the friction delay
                        SuspendProcess(processName);
                        break;

                    case EnforcementAction.Allow:
                        break;
                }
            }, _log, $"ProcessWatcher_Evaluate_{processName}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error in OnProcessCreated");
        }
    }

    private static void KillByName(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try { p.Kill(); } catch { }
        }
    }

    private static void SuspendProcess(string processName)
    {
        // Suspend all threads of the new process so the user sees nothing
        // until the friction overlay resolves
        var name = Path.GetFileNameWithoutExtension(processName);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try
            {
                foreach (System.Diagnostics.ProcessThread thread in p.Threads)
                {
                    var hThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)thread.Id);
                    if (hThread != IntPtr.Zero)
                    {
                        SuspendThread(hThread);
                        CloseHandle(hThread);
                    }
                }
            }
            catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr hThread);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [Flags]
    private enum ThreadAccess : int { SUSPEND_RESUME = 0x0002 }
}
