using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ConfigUI.Helpers;

/// <summary>P/Invoke helpers to pin friction overlay above all windows.</summary>
internal static class OverlayWindowHelper
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    public static void EnforceTopmost(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyTopmost(window);
            return;
        }
        ApplyTopmost(window);
    }

    private static void ApplyTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }
}
