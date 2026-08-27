using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cetus.Platform;

internal enum WindowBackdropKind
{
    None,
    Windows10Blur,
    Windows11DesktopAcrylic,
}

/// <summary>
/// Owns the native HWND composition policy. Windows 11 uses the documented
/// system backdrop; Windows 10 uses the lighter blur-behind accent state rather
/// than the acrylic accent state that stalls window movement on stable builds.
/// </summary>
internal sealed class WindowComposition : IDisposable
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmSystemBackdropType = 38;
    private const int DwmCornerDoNotRound = 1;
    private const int DwmBackdropTransientWindow = 3;
    private const int WindowCompositionAccentPolicy = 19;
    private const uint DwmBbEnable = 0x00000001;

    private readonly HwndSource _source;
    private readonly Action _taskbarRecreated;
    private readonly uint _taskbarCreatedMessage;
    private bool _disposed;

    private WindowComposition(HwndSource source, Action taskbarRecreated)
    {
        _source = source;
        _taskbarRecreated = taskbarRecreated;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _source.AddHook(WindowMessageHook);
        BackdropKind = ApplyNativeComposition(_source);
    }

    public WindowBackdropKind BackdropKind { get; }

    public static WindowComposition Attach(Window window, Action taskbarRecreated)
    {
        ArgumentNullException.ThrowIfNull(window);
        HwndSource source = PresentationSource.FromVisual(window) as HwndSource
            ?? throw new InvalidOperationException("窗口 HWND 尚未初始化。");
        return new WindowComposition(source, taskbarRecreated);
    }

    public void SetDarkMode(bool isDark)
    {
        int enabled = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(
            _source.Handle,
            DwmUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (!_disposed && (uint)message == _taskbarCreatedMessage)
        {
            _taskbarRecreated();
        }
        return IntPtr.Zero;
    }

    private static WindowBackdropKind ApplyNativeComposition(HwndSource source)
    {
        if (source.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
        }

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(source.Handle, ref margins);

        int squareCorners = DwmCornerDoNotRound;
        _ = DwmSetWindowAttribute(
            source.Handle,
            DwmWindowCornerPreference,
            ref squareCorners,
            sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int backdrop = DwmBackdropTransientWindow;
            if (DwmSetWindowAttribute(
                    source.Handle,
                    DwmSystemBackdropType,
                    ref backdrop,
                    sizeof(int)) == 0)
            {
                return WindowBackdropKind.Windows11DesktopAcrylic;
            }
        }

        if (TryEnableWindows10Blur(source.Handle))
        {
            return WindowBackdropKind.Windows10Blur;
        }

        return EnableDwmBlurFallback(source.Handle)
            ? WindowBackdropKind.Windows10Blur
            : WindowBackdropKind.None;
    }

    private static bool TryEnableWindows10Blur(IntPtr hwnd)
    {
        if (!NativeLibrary.TryLoad("user32.dll", out IntPtr user32))
        {
            return false;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(
                    user32,
                    "SetWindowCompositionAttribute",
                    out IntPtr export))
            {
                return false;
            }

            var setAttribute = Marshal.GetDelegateForFunctionPointer<SetWindowCompositionAttributeDelegate>(export);
            var accent = new AccentPolicy
            {
                State = AccentState.EnableBlurBehind,
                Flags = 0,
                GradientColor = 0,
                AnimationId = 0,
            };
            IntPtr accentPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
            try
            {
                Marshal.StructureToPtr(accent, accentPointer, fDeleteOld: false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAccentPolicy,
                    Data = accentPointer,
                    SizeOfData = Marshal.SizeOf<AccentPolicy>(),
                };
                return setAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPointer);
            }
        }
        finally
        {
            NativeLibrary.Free(user32);
        }
    }

    private static bool EnableDwmBlurFallback(IntPtr hwnd)
    {
        try
        {
            var blur = new DwmBlurBehind
            {
                Flags = DwmBbEnable,
                Enable = true,
                BlurRegion = IntPtr.Zero,
                TransitionOnMaximized = false,
            };
            return DwmEnableBlurBehindWindow(hwnd, ref blur) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WindowMessageHook);
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState State;
        public int Flags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;

        public IntPtr BlurRegion;

        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetWindowCompositionAttributeDelegate(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        IntPtr hwnd,
        ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(
        IntPtr hwnd,
        ref DwmBlurBehind blurBehind);
}
