using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cetus.Browser;
using Cetus.Configuration;
using Cetus.Platform;
using Cetus.Presentation;
using Cetus.Runtime;
using Cetus.Updates;
using Microsoft.Win32;

namespace Cetus;

/// <summary>
/// Thin WPF view. Runtime coordination, WebView2 policy and native Windows
/// integration live in their respective modules.
/// </summary>
public partial class MainWindow : Window
{
    // Snappier than DSH's 300ms: fewer per-frame cross-process resizes of the
    // hosted WebView means less visible stutter on the push layout. Expand
    // decelerates into place (instant response), collapse accelerates away
    // (snappy exit) and runs shorter.
    private static readonly Duration SidebarExpandDuration =
        new(TimeSpan.FromMilliseconds(240));

    private static readonly Duration SidebarCollapseDuration =
        new(TimeSpan.FromMilliseconds(190));

    private static readonly KeySpline SidebarExpandSpline = FreezeSpline(new KeySpline(0, 0, 0.2, 1));

    private static readonly KeySpline SidebarCollapseSpline = FreezeSpline(new KeySpline(0.4, 0, 1, 1));

    private static KeySpline FreezeSpline(KeySpline spline)
    {
        spline.Freeze();
        return spline;
    }

    /// <summary>Client width the DSH page keeps while the panel is expanded.</summary>
    private const double MinimumDshSurfaceWidth = 480;

    /// <summary>
    /// Sidebar cap for the current window: grows with the window (fullscreen
    /// allows a much wider panel) but always leaves the DSH surface usable.
    /// </summary>
    private double EffectiveSidebarMax =>
        Math.Clamp(
            ActualWidth - MinimumDshSurfaceWidth,
            CetusSettings.MinimumRightSidebarWidth,
            CetusSettings.MaximumRightSidebarWidth);

    private readonly CetusSettings _settings;
    private readonly BrowserSession _browserSession;
    private readonly DesktopRuntime _runtime;

    private TrayIconController? _tray;
    private WindowComposition? _windowComposition;
    private UpdateCoordinator? _updates;
    private bool _isExiting;
    private bool _rightSidebarOpen;
    private int _rightSidebarAnimationGeneration;
    private bool _startupStarted;

    /// <summary>
    /// Raised when the splash screen should go away: either the runtime
    /// settled (ready or failed) or the user asked for the window early.
    /// </summary>
    public event EventHandler? SplashDismissRequested;

    public MainWindow()
    {
        _settings = CetusSettings.LoadDefault();
        InitializeComponent();
        InitializeRightSidebar();
        SizeChanged += (_, _) => ClampSidebarWidthToWindow();

        _browserSession = new BrowserSession(
            Browser,
            ApplyWindowTheme,
            OnRightSidebarToggleRequested,
            OnDshModalStateChanged);
        _browserSession.SetRightSidebarOpen(_rightSidebarOpen);
        _runtime = new DesktopRuntime(_settings, _browserSession, Dispatcher);
        _runtime.StateChanged += OnRuntimeStateChanged;
        RightSidebarContent.SetDshEndpointProvider(() => _runtime.Endpoint);

        if (DevModeFlag.IsActive)
        {
            Title = "CETUS鲸鱼座 · DEV";
            TitleText.Text = "CETUS DEV";
        }

        ApplyWindowTheme(IsSystemDarkMode());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // Composition attaches at Loaded — EnsureHandle() raises this event
        // before an HwndSource exists, and the splash flow keeps the window
        // hidden until after startup anyway.
        base.OnSourceInitialized(e);
    }

    /// <summary>
    /// Begins startup while the brand splash is showing; the main window
    /// appears only after the runtime settles.
    /// </summary>
    public void StartStartup()
    {
        if (_startupStarted || _isExiting)
        {
            return;
        }

        _startupStarted = true;
        SetupTray();
        // Create the native HWND (and the WebView2 host surface) without
        // showing the window — EnsureCoreWebView2Async would otherwise wait
        // forever for a parent handle while the splash is up.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        _ = RunStartupAsync();
        if (_settings.CheckUpdatesOnStartup)
        {
            _updates ??= new UpdateCoordinator(this, ExitApplication, _settings);
            _ = CheckForUpdatesSilentlyAsync();
        }
    }

    private async Task RunStartupAsync()
    {
        // Splash phase: bring the DSH host up with the window still hidden —
        // WebView2 cannot initialize on a window that was never shown.
        DesktopRuntimeResult result = await _runtime.StartAsync(navigateWithUi: false);
        if (_isExiting)
        {
            return;
        }

        SplashDismissRequested?.Invoke(this, EventArgs.Empty);
        Show();
        ShowRuntimeError(result, "Cetus · 启动失败");
        if (!result.Succeeded)
        {
            return;
        }

        // Visible phase: load the DSH page with the status line showing.
        try
        {
            await _runtime.NavigateHomeAsync();
        }
        catch (Exception error)
        {
            ShowRuntimeError(DesktopRuntimeResult.Failed(error), "Cetus · 启动失败");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_windowComposition is null)
        {
            _windowComposition = WindowComposition.Attach(
                this,
                () =>
                {
                    if (!_isExiting)
                    {
                        _tray?.RestoreAfterExplorerRestart();
                    }
                });
            _windowComposition.SetDarkMode(IsSystemDarkMode());
        }

        // The splash flow starts the runtime before the window is shown; this
        // only fires on the legacy path where the window appears unprompted.
        if (_startupStarted)
        {
            return;
        }

        SetupTray();
        DesktopRuntimeResult result = await _runtime.StartAsync();
        ShowRuntimeError(result, "Cetus · 启动失败");
        if (_settings.CheckUpdatesOnStartup)
        {
            _updates ??= new UpdateCoordinator(this, ExitApplication, _settings);
            _ = CheckForUpdatesSilentlyAsync();
        }
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            await _updates!.CheckForUpdatesAsync(interactive: false);
        }
        catch
        {
            // Startup update checks must never surface as errors.
        }
    }

    private void OnRuntimeStateChanged(object? sender, DesktopRuntimeStateChangedEventArgs e)
    {
        DesktopRuntimeState state = e.State;
        _tray?.SetRetryEnabled(state.CanRetry);

        if (state.Phase == DesktopRuntimePhase.Ready)
        {
            StatusText.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(state.Message))
        {
            StatusText.Text = state.Message;
        }
    }

    private void SetupTray()
    {
        if (_tray is not null)
        {
            return;
        }

        _updates ??= new UpdateCoordinator(this, ExitApplication, _settings);
        _tray = new TrayIconController(new TrayCommands(
            ShowWindow,
            RetryDshAsync,
            ConfigurePortAsync,
            () => _updates.CheckForUpdatesAsync(interactive: true),
            ExitApplication));
        _tray.SetRetryEnabled(_runtime.State.CanRetry);
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void OnRightSidebarToggleRequested() =>
        SetRightSidebarOpen(!_rightSidebarOpen, animate: true);

    private void OnDshModalStateChanged(bool isOpen) =>
        RightSidebarContent.SetModalDim(isOpen);

    private void OnMaximizeClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.MaximizeWindow(this);

    private void OnRestoreClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.RestoreWindow(this);

    private void OnCloseToTrayClicked(object sender, RoutedEventArgs e) => Close();

    private void InitializeRightSidebar()
    {
        // The panel always starts collapsed; only its width is remembered.
        _rightSidebarOpen = false;
        ApplyRightSidebarLayout(_rightSidebarOpen, _settings.RightSidebarWidth);
    }

    private void ClampSidebarWidthToWindow()
    {
        if (!_rightSidebarOpen)
        {
            return;
        }

        RightSidebarColumn.MaxWidth = EffectiveSidebarMax;
        if (RightSidebarColumn.ActualWidth > EffectiveSidebarMax)
        {
            RightSidebarColumn.Width = new GridLength(EffectiveSidebarMax, GridUnitType.Pixel);
        }
    }

    private void SetRightSidebarOpen(bool isOpen, bool animate)
    {
        _rightSidebarOpen = isOpen;
        _browserSession.SetRightSidebarOpen(isOpen);

        double currentWidth = Math.Clamp(
            RightSidebarColumn.ActualWidth,
            0,
            EffectiveSidebarMax);
        double targetWidth = isOpen
            ? Math.Clamp(_settings.RightSidebarWidth, CetusSettings.MinimumRightSidebarWidth, EffectiveSidebarMax)
            : 0;
        int generation = ++_rightSidebarAnimationGeneration;

        RightSidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        RightSidebarColumn.MinWidth = 0;
        RightSidebarColumn.Width = new GridLength(currentWidth, GridUnitType.Pixel);
        RightSidebarResizeThumb.IsEnabled = false;

        bool shouldAnimate = animate
            && SystemParameters.ClientAreaAnimation
            && Math.Abs(currentWidth - targetWidth) >= 1;
        if (!shouldAnimate)
        {
            ApplyRightSidebarLayout(isOpen, targetWidth);
            return;
        }

        // DSH sidebar method (slide, not morph): the panel holds its full
        // expanded layout while the column clips it against the window edge,
        // so nothing re-wraps mid-slide and the hosted WebView2 windows move
        // instead of resizing every frame.
        RightSidebarContent.Width = isOpen ? targetWidth : currentWidth;
        RightSidebarContent.HorizontalAlignment = HorizontalAlignment.Left;
        // Hover restyles mid-slide only burn frames; freeze interaction too.
        RightSidebarContent.IsHitTestVisible = false;

        var animation = new GridLengthAnimation
        {
            From = new GridLength(currentWidth, GridUnitType.Pixel),
            To = new GridLength(targetWidth, GridUnitType.Pixel),
            Duration = isOpen ? SidebarExpandDuration : SidebarCollapseDuration,
            Spline = isOpen ? SidebarExpandSpline : SidebarCollapseSpline,
            // Hold the final value through the completion callback: with Stop
            // the column snapped back to its start width for one frame.
            FillBehavior = FillBehavior.HoldEnd,
        };
        // Layout-driven animation: cap the tick rate so high-refresh monitors
        // do not pay double relayout cost for imperceptible extra frames.
        Timeline.SetDesiredFrameRate(animation, 60);
        animation.Completed += (_, _) =>
        {
            if (generation == _rightSidebarAnimationGeneration)
            {
                ApplyRightSidebarLayout(isOpen, targetWidth);
            }
        };
        RightSidebarColumn.BeginAnimation(
            ColumnDefinition.WidthProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyRightSidebarLayout(bool isOpen, double width)
    {
        ++_rightSidebarAnimationGeneration;
        // Release the mid-slide frozen layout (idempotent for the
        // non-animated paths) so the panel stretches with its column again.
        RightSidebarContent.ClearValue(WidthProperty);
        RightSidebarContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        RightSidebarContent.IsHitTestVisible = true;
        RightSidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        RightSidebarContent.UpdateEmptyState();
        if (isOpen)
        {
            double clampedWidth = Math.Clamp(
                width,
                CetusSettings.MinimumRightSidebarWidth,
                EffectiveSidebarMax);
            RightSidebarColumn.MinWidth = CetusSettings.MinimumRightSidebarWidth;
            RightSidebarColumn.MaxWidth = EffectiveSidebarMax;
            RightSidebarColumn.Width = new GridLength(clampedWidth, GridUnitType.Pixel);
            RightSidebarResizeThumb.IsEnabled = true;
        }
        else
        {
            RightSidebarColumn.MinWidth = 0;
            RightSidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            RightSidebarResizeThumb.IsEnabled = false;
        }
    }

    private void OnRightSidebarResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_rightSidebarOpen)
        {
            return;
        }

        RightSidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        double width = Math.Clamp(
            RightSidebarColumn.ActualWidth - e.HorizontalChange,
            CetusSettings.MinimumRightSidebarWidth,
            EffectiveSidebarMax);
        RightSidebarColumn.Width = new GridLength(width, GridUnitType.Pixel);
    }

    private void OnRightSidebarResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        double width = Math.Clamp(
            RightSidebarColumn.ActualWidth,
            CetusSettings.MinimumRightSidebarWidth,
            CetusSettings.MaximumRightSidebarWidth);
        RightSidebarColumn.Width = new GridLength(width, GridUnitType.Pixel);
        _settings.SetRightSidebarWidth(width);
        _settings.SetRightSidebarWidth(width);
    }

    private void ShowWindow()
    {
        SplashDismissRequested?.Invoke(this, EventArgs.Empty);
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task RetryDshAsync()
    {
        if (_isExiting)
        {
            return;
        }

        ShowWindow();
        DesktopRuntimeResult result = await _runtime.RetryAsync();
        ShowRuntimeError(result, "Cetus · 启动失败");
    }

    private async Task ConfigurePortAsync()
    {
        if (_runtime.IsBusy || _isExiting)
        {
            return;
        }

        ShowWindow();
        var dialog = new PortSettingsDialog(
            _settings.ConfiguredPort,
            _settings.EffectivePort,
            _settings.IsPortOverridden)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedPort is not int port)
        {
            return;
        }

        PortChangeResult result = await _runtime.ChangePortAsync(port);
        if (!result.Saved)
        {
            ShowRuntimeError(result.ReconnectResult, "Cetus · 无法保存端口设置");
            return;
        }

        if (result.IsEnvironmentOverridden)
        {
            _ = MessageBox.Show(
                this,
                "端口设置已保存。当前进程仍受 CETUS_PORT 环境变量覆盖；移除该变量后，保存值将在下次启动时生效。",
                "Cetus · 端口设置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ShowRuntimeError(result.ReconnectResult, "Cetus · 重连失败");
    }

    private void ShowRuntimeError(DesktopRuntimeResult result, string title)
    {
        if (result.Error is not { } error)
        {
            return;
        }

        _ = MessageBox.Show(
            this,
            error.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int mode && mode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyWindowTheme(bool isDark)
    {
        _windowComposition?.SetDarkMode(isDark);
        Resources["TitleBarTintBrush"] = CreateBrush(
            isDark ? "#151517" : "#F5F7FA",
            isDark ? 0.70 : 0.78);
        Resources["TitleForegroundBrush"] = CreateBrush(isDark ? "#E0F5F7FA" : "#E6172033");
        Resources["CaptionForegroundBrush"] = CreateBrush(isDark ? "#E0F5F7FA" : "#D6172033");
        Resources["CaptionHoverBrush"] = CreateBrush(isDark ? "#2EFFFFFF" : "#14000000");
        Resources["CaptionPressedBrush"] = CreateBrush(isDark ? "#4AFFFFFF" : "#24000000");
        Resources["CaptionFocusBrush"] = CreateBrush(isDark ? "#24FFFFFF" : "#10000000");
        Resources["SidebarBorderBrush"] = CreateBrush(isDark ? "#2AFFFFFF" : "#1C000000");
        Resources["RightSidebarBackgroundBrush"] = CreateBrush(isDark ? "#1B1B1C" : "#F5F7FA");
        Resources["SidebarPanelForegroundBrush"] = CreateBrush(isDark ? "#F9FAFB" : "#0F1115");
        Resources["SidebarPanelSecondaryBrush"] = CreateBrush(isDark ? "#CFD3D6" : "#61666B");
        Resources["SidebarPanelSelectedBrush"] = CreateBrush(isDark ? "#2EFFFFFF" : "#12000000");
        Resources["SidebarPanelInputBrush"] = CreateBrush(isDark ? "#222224" : "#FFFFFF");
        Resources["SidebarTerminalBackgroundBrush"] = CreateBrush(isDark ? "#101011" : "#F8FAFC");
        Resources["SidebarTerminalForegroundBrush"] = CreateBrush(isDark ? "#E5E7EB" : "#172033");
        WindowFrame.Background = CreateBrush(isDark ? "#151517" : "#F8FAFC");
        StatusText.Foreground = CreateBrush(isDark ? "#AAB7CC" : "#52627A");
        // Match the frame so Chromium's repaint lag during window/sidebar
        // resizes shows themed bands instead of a flashing white sliver.
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            255,
            isDark ? 27 : 245,
            isDark ? 27 : 247,
            isDark ? 28 : 250);
        RightSidebarContent.ApplyTheme(isDark);
    }

    private static System.Windows.Media.Brush CreateBrush(string color, double opacity = 1) =>
        new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color))
        {
            Opacity = opacity,
        };

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
        }
        base.OnClosing(e);
    }

    private void ExitApplication() => _ = ExitApplicationAsync();

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _tray?.Dispose();
        _tray = null;
        RightSidebarContent.Dispose();
        await _runtime.StopAsync();
        _browserSession.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isExiting = true;
        _runtime.StateChanged -= OnRuntimeStateChanged;
        _tray?.Dispose();
        _tray = null;
        _windowComposition?.Dispose();
        _windowComposition = null;
        RightSidebarContent.Dispose();
        _ = StopAfterUnexpectedCloseAsync();
        base.OnClosed(e);
    }

    private async Task StopAfterUnexpectedCloseAsync()
    {
        try
        {
            await _runtime.StopAsync();
        }
        catch
        {
            // The window is already closing; the sidecar Job Object remains the
            // authoritative cleanup for the DSH process tree.
        }

        _browserSession.Dispose();
    }
}
