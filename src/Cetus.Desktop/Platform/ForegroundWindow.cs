using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cetus.Platform;

/// <summary>
/// Foreground-window checks. Used to keep tray notifications quiet while the
/// user is actively looking at CETUS.
/// </summary>
internal static class ForegroundWindow
{
    public static bool IsCurrent(Window window)
    {
        IntPtr foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero
            && foreground == new WindowInteropHelper(window).Handle;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
