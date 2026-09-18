using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cetus.Platform;

/// <summary>
/// Registers the process-wide Ctrl+Alt+Space hotkey against an existing
/// HWND and translates WM_HOTKEY into <see cref="ToggleRequested"/>.
/// Registration fails silently when another application owns the combo.
/// </summary>
internal sealed class GlobalHotkeyManager : IDisposable
{
    private const int HotkeyId = 0x4355; // "CU"
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkeyManager(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    public event Action? ToggleRequested;

    public bool IsRegistered => _registered;

    /// <summary>Registers the hotkey; returns false when the combo is taken.</summary>
    public bool Register()
    {
        if (_disposed)
        {
            return false;
        }

        if (_registered)
        {
            return true;
        }

        _registered = RegisterHotKey(_source.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered || _disposed)
        {
            return;
        }

        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            ToggleRequested?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
