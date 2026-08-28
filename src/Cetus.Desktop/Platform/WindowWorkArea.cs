namespace Cetus.Platform;

internal readonly record struct NativeBounds(int Left, int Top, int Right, int Bottom);

internal readonly record struct MaximizedWindowBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Converts monitor coordinates into the monitor-relative bounds expected by
/// WM_GETMINMAXINFO, preserving taskbars docked to any screen edge.
/// </summary>
internal static class WindowWorkArea
{
    public static MaximizedWindowBounds Calculate(
        NativeBounds monitor,
        NativeBounds workArea) =>
        new(
            workArea.Left - monitor.Left,
            workArea.Top - monitor.Top,
            workArea.Right - workArea.Left,
            workArea.Bottom - workArea.Top);
}
