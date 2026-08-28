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
    private static readonly Duration RightSidebarAnimationDuration =
        new(TimeSpan.FromMilliseconds(300));

    private readonly CetusSettings _settings;
    private readonly BrowserSession _browserSession;
    private readonly DesktopRuntime _runtime;

    private TrayIconController? _tray;
    private WindowComposition? _windowComposition;
    private UpdateCoordinator? _updates;
    private bool _isExiting;
    private bool _rightSidebarOpen;
    private int _rightSidebarAnimationGeneration;

    public MainWindow()
    {
        _settings = CetusSettings.LoadDefault();
        InitializeComponent();
        InitializeRightSidebar();

        _browserSession = new BrowserSession(
            Browser,
            ApplyWindowTheme,
            OnRightSidebarToggleRequested);
        _browserSession.SetRightSidebarOpen(_rightSidebarOpen);
        _runtime = new DesktopRuntime(_settings, _browserSession, Dispatcher);
        _runtime.StateChanged += OnRuntimeStateChanged;

        if (DevModeFlag.IsActive)
        {
            Title = "CETUS鲸鱼座 · DEV";
            TitleText.Text = "CETUS DEV";
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 40;
            Top = 40;
        }

        ApplyWindowTheme(IsSystemDarkMode());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        DesktopRuntimeResult result = await _runtime.StartAsync();
        ShowRuntimeError(result, "Cetus · 启动失败");
        if (_settings.CheckUpdatesOnStartup)
        {
            _updates ??= new UpdateCoordinator(this, ExitApplication);
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

        _updates ??= new UpdateCoordinator(this, ExitApplication);
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

    private void OnMaximizeClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.MaximizeWindow(this);

    private void OnRestoreClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.RestoreWindow(this);

    private void OnCloseToTrayClicked(object sender, RoutedEventArgs e) => Close();

    private void InitializeRightSidebar()
    {
        _rightSidebarOpen = _settings.RightSidebarOpen;
        ApplyRightSidebarLayout(_rightSidebarOpen, _settings.RightSidebarWidth);
    }

    private void SetRightSidebarOpen(bool isOpen, bool animate)
    {
        _rightSidebarOpen = isOpen;
        _settings.SetRightSidebarOpen(isOpen);
        _browserSession.SetRightSidebarOpen(isOpen);

        double currentWidth = Math.Clamp(
            RightSidebarColumn.ActualWidth,
            0,
            CetusSettings.MaximumRightSidebarWidth);
        double targetWidth = isOpen ? _settings.RightSidebarWidth : 0;
        int generation = ++_rightSidebarAnimationGeneration;

        RightSidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        RightSidebarColumn.MinWidth = 0;
        RightSidebarColumn.Width = new GridLength(currentWidth, GridUnitType.Pixel);
        RightSidebarSplitter.IsEnabled = false;
        if (isOpen)
        {
            SetRightSidebarDivider(isOpen: true);
        }

        bool shouldAnimate = animate
            && SystemParameters.ClientAreaAnimation
            && Math.Abs(currentWidth - targetWidth) >= 1;
        if (!shouldAnimate)
        {
            ApplyRightSidebarLayout(isOpen, targetWidth);
            return;
        }

        var animation = new GridLengthAnimation
        {
            From = new GridLength(currentWidth, GridUnitType.Pixel),
            To = new GridLength(targetWidth, GridUnitType.Pixel),
            Duration = RightSidebarAnimationDuration,
            FillBehavior = FillBehavior.Stop,
        };
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
        RightSidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        if (isOpen)
        {
            double clampedWidth = Math.Clamp(
                width,
                CetusSettings.MinimumRightSidebarWidth,
                CetusSettings.MaximumRightSidebarWidth);
            RightSidebarColumn.MinWidth = CetusSettings.MinimumRightSidebarWidth;
            RightSidebarColumn.MaxWidth = CetusSettings.MaximumRightSidebarWidth;
            RightSidebarColumn.Width = new GridLength(clampedWidth, GridUnitType.Pixel);
            SetRightSidebarDivider(isOpen: true);
            RightSidebarSplitter.IsEnabled = true;
        }
        else
        {
            RightSidebarColumn.MinWidth = 0;
            RightSidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SetRightSidebarDivider(isOpen: false);
            RightSidebarSplitter.IsEnabled = false;
        }
    }

    private void SetRightSidebarDivider(bool isOpen)
    {
        RightSidebarDividerColumn.Width = new GridLength(isOpen ? 8 : 0, GridUnitType.Pixel);
        RightSidebarSplitter.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRightSidebarDragStarted(object sender, DragStartedEventArgs e)
    {
        ++_rightSidebarAnimationGeneration;
        double currentWidth = RightSidebarColumn.ActualWidth;
        RightSidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        RightSidebarColumn.Width = new GridLength(currentWidth, GridUnitType.Pixel);
    }

    private void OnRightSidebarDragCompleted(object sender, DragCompletedEventArgs e)
    {
        double width = Math.Clamp(
            RightSidebarColumn.ActualWidth,
            CetusSettings.MinimumRightSidebarWidth,
            CetusSettings.MaximumRightSidebarWidth);
        RightSidebarColumn.Width = new GridLength(width, GridUnitType.Pixel);
        _settings.SetRightSidebarWidth(width);
    }

    private void ShowWindow()
    {
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
        Resources["SidebarHandleBrush"] = CreateBrush(isDark ? "#24FFFFFF" : "#14000000");
        Resources["SidebarHandleHoverBrush"] = CreateBrush(isDark ? "#3AFFFFFF" : "#28000000");
        WindowFrame.Background = CreateBrush(isDark ? "#151517" : "#F8FAFC");
        StatusText.Foreground = CreateBrush(isDark ? "#AAB7CC" : "#52627A");
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
