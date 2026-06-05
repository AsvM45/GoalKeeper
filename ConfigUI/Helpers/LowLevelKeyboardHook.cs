using System.Runtime.InteropServices;

namespace ConfigUI.Helpers;

/// <summary>Blocks Alt+F4 and Win+D bypass attempts while friction overlay is active.</summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F4 = 0x73;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_D = 0x44;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _blockBypassKeys = true;

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install() => _hookId = SetHook(_proc);

    public void SetBlocking(bool enabled) => _blockBypassKeys = enabled;

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _blockBypassKeys &&
            (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);
            bool altHeld = (GetAsyncKeyState(0x12) & 0x8000) != 0;   // VK_MENU
            bool winHeld  = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                         || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            if ((altHeld && vk == VK_F4) || (winHeld && vk == VK_D) || vk == VK_LWIN || vk == VK_RWIN)
                return (IntPtr)1;
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
