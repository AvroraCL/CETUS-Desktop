using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Cetus.Configuration;
using Cetus.Hosting;
using Microsoft.Web.WebView2.Core;
using WpfMessageBox = System.Windows.MessageBox;

namespace Cetus;

/// <summary>
/// Main window: owns the DSH host lifecycle, the WebView2 surface, and the tray icon.
/// Closing the window hides to tray; the tray "退出" kills the host and exits.
/// </summary>
public partial class MainWindow : Window
{
    // The per-install persisted setting is used unless CETUS_PORT supplies a
    // process-only override for automation or isolated test runs.
    private string DshUrl => $"http://127.0.0.1:{_settings.EffectivePort}/";

    private const int MaxAutomaticRestartAttempts = 3;
    private static readonly TimeSpan StableRuntimeWindow = TimeSpan.FromMinutes(1);

    private readonly CetusSettings _settings;
    private DshHost? _host;
    private NotifyIcon? _tray;
    private HwndSource? _windowSource;
    private uint _taskbarCreatedMessage;
    private ToolStripMenuItem? _retryDshItem;
    private bool _isStarting;
    private bool _isExiting;
    private bool _navigationPolicyAttached;
    private int _automaticRestartAttempts;
    private DateTime _lastDshReadyAt;
    private CancellationTokenSource? _startupCancellation;
    private CancellationTokenSource? _recoveryCancellation;

    public MainWindow()
    {
        _settings = CetusSettings.LoadDefault();
        InitializeComponent();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_isExiting && (uint)message == _taskbarCreatedMessage)
        {
            RestoreTrayIcon();
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        await StartAsync();
    }

    private async Task<bool> StartAsync(bool showError = true)
    {
        if (_isStarting || _isExiting)
        {
            return false;
        }

        _isStarting = true;
        if (_retryDshItem is not null)
        {
            _retryDshItem.Enabled = false;
        }

        var cancellation = new CancellationTokenSource();
        _startupCancellation = cancellation;
        try
        {
            StatusText.Text = "正在启动 DSH 主机…";
            // Resolve DSH here so missing runtime/configuration errors reach the
            // normal startup error UI instead of terminating the application.
            DshHost host = _host ??= CreateDshHost();
            await host.StartAsync(cancellation.Token);

            StatusText.Text = "正在加载界面…";
            // Explicit user data folder: WebView2's default (next to the exe)
            // would pollute the install directory and survive uninstall.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cetus", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            cancellation.Token.ThrowIfCancellationRequested();
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            AttachNavigationPolicy();
            Browser.CoreWebView2.Navigate(DshUrl);

            Browser.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
            _lastDshReadyAt = DateTime.UtcNow;
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await StopHostAsync();
            StatusText.Text = _isExiting ? "正在退出…" : "启动已取消";
            return false;
        }
        catch (Exception error)
        {
            // If WebView2 initialization fails after Cetus started DSH, release
            // that owned sidecar rather than leaving it behind in the background.
            await StopHostAsync();
            StatusText.Text = "启动失败";
            if (showError)
            {
                _ = WpfMessageBox.Show(
                    this,
                    error.Message,
                    "Cetus · 启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_startupCancellation, cancellation))
            {
                _startupCancellation = null;
            }
            cancellation.Dispose();
            _isStarting = false;
            if (_retryDshItem is not null)
            {
                _retryDshItem.Enabled = true;
            }
        }
    }

    private async Task StopHostAsync()
    {
        if (_host is { } host)
        {
            await host.StopAsync();
        }
    }

    private void CancelStartup() => _startupCancellation?.Cancel();

    private void CancelAutomaticRecovery()
    {
        CancellationTokenSource? cancellation = _recoveryCancellation;
        _recoveryCancellation = null;
        cancellation?.Cancel();
    }

    /// <summary>
    /// Keep the embedded browser on Cetus's loopback DSH origin. Top-level
    /// external links and every popup request are delegated to the system browser;
    /// external child frames are blocked rather than opened invisibly.
    /// </summary>
    private void AttachNavigationPolicy()
    {
        if (_navigationPolicyAttached)
        {
            return;
        }

        Browser.CoreWebView2.NavigationStarting += OnTopLevelNavigationStarting;
        Browser.CoreWebView2.FrameNavigationStarting += OnFrameNavigationStarting;
        Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        _navigationPolicyAttached = true;
    }

    private void OnTopLevelNavigationStarting(
        object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsDshUri(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenInSystemBrowser(e.Uri);
    }

    private void OnFrameNavigationStarting(
        object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsDshUri(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(
        object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenInSystemBrowser(e.Uri);
    }

    private bool IsDshUri(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        var expected = new Uri(DshUrl);
        return string.Equals(uri.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == expected.Port
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static void OpenInSystemBrowser(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // External launch is best effort; the in-app navigation remains blocked.
        }
    }

    private DshHost CreateDshHost()
    {
        var host = new DshHost(DshLocator.Resolve(), DshUrl, _settings.DshHomeOverride);
        host.UnexpectedExit += OnDshHostUnexpectedExit;
        return host;
    }

    private void OnDshHostUnexpectedExit(object? sender, DshHostExitedEventArgs e)
    {
        if (_isExiting || !ReferenceEquals(sender, _host) || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => BeginAutomaticRecovery(e));
    }

    private void BeginAutomaticRecovery(DshHostExitedEventArgs exit)
    {
        if (_isExiting || _isStarting)
        {
            return;
        }

        CancelAutomaticRecovery();
        var cancellation = new CancellationTokenSource();
        _recoveryCancellation = cancellation;
        _ = RecoverFromUnexpectedExitAsync(exit, cancellation);
    }

    private async Task RecoverFromUnexpectedExitAsync(
        DshHostExitedEventArgs exit, CancellationTokenSource cancellation)
    {
        try
        {
            if (DateTime.UtcNow - _lastDshReadyAt >= StableRuntimeWindow)
            {
                _automaticRestartAttempts = 0;
            }

            while (_automaticRestartAttempts < MaxAutomaticRestartAttempts)
            {
                int attempt = ++_automaticRestartAttempts;
                int delaySeconds = attempt * 2;
                Browser.Visibility = Visibility.Collapsed;
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text =
                    $"DSH 主机意外退出（代码 {exit.ExitCode}）；将在 {delaySeconds} 秒后尝试恢复（{attempt}/{MaxAutomaticRestartAttempts}）…";

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellation.Token);
                if (_isExiting || cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (await RetryDshAsync(isAutomatic: true))
                {
                    return;
                }
            }

            if (!_isExiting && !cancellation.IsCancellationRequested)
            {
                StatusText.Text =
                    "DSH 多次启动失败，已停止自动恢复。请在托盘菜单中选择“重试连接 DSH”。";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A manual retry or application exit cancelled this recovery chain.
        }
        finally
        {
            if (ReferenceEquals(_recoveryCancellation, cancellation))
            {
                _recoveryCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private static System.Drawing.Icon ResolveTrayIcon()
    {
        string? executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
            catch
            {
                // Fall back to the standard application icon below.
            }
        }

        return SystemIcons.Application;
    }

    private void RestoreTrayIcon()
    {
        if (_tray is not { } tray)
        {
            return;
        }

        // Explorer loses notification-area registrations when it restarts.
        // Toggling Visible re-registers the existing NotifyIcon instance.
        tray.Visible = false;
        tray.Visible = true;
    }

    private void SetupTray()
    {
        // Use the brand icon embedded in the exe (ApplicationIcon) for the tray.
        _tray = new NotifyIcon
        {
            Icon = ResolveTrayIcon(),
            Text = "Cetus · 鲸鱼座",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => ShowWindow());
        _retryDshItem = new ToolStripMenuItem("重试连接 DSH");
        _retryDshItem.Click += async (_, _) => await RetryDshAsync();
        menu.Items.Add(_retryDshItem);
        var configurePortItem = new ToolStripMenuItem("设置 DSH 端口…");
        configurePortItem.Click += async (_, _) => await ConfigurePortAsync();
        menu.Items.Add(configurePortItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Retry the DSH connection. An owned sidecar is stopped first and then
    /// started again; an externally managed healthy DSH remains untouched and
    /// is simply reconnected.
    /// </summary>
    private async Task<bool> RetryDshAsync(bool isAutomatic = false, bool recreateHost = false)
    {
        if (_isExiting)
        {
            return false;
        }
        if (_isStarting)
        {
            if (!isAutomatic)
            {
                CancelStartup();
            }
            return false;
        }

        if (!isAutomatic)
        {
            _automaticRestartAttempts = 0;
            CancelAutomaticRecovery();
            ShowWindow();
        }

        Browser.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        await StopHostAsync();
        if (recreateHost)
        {
            _host = null;
        }
        return await StartAsync(showError: !isAutomatic);
    }

    private async Task ConfigurePortAsync()
    {
        if (_isStarting || _isExiting)
        {
            return;
        }

        ShowWindow();
        var dialog = new PortSettingsDialog(
            _settings.ConfiguredPort, _settings.EffectivePort, _settings.IsPortOverridden)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedPort is not int port)
        {
            return;
        }

        try
        {
            _settings.SetConfiguredPort(port);
        }
        catch (Exception error)
        {
            _ = WpfMessageBox.Show(
                this,
                error.Message,
                "Cetus · 无法保存端口设置",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (_settings.IsPortOverridden)
        {
            _ = WpfMessageBox.Show(
                this,
                "端口设置已保存。当前进程仍受 CETUS_PORT 环境变量覆盖；移除该变量后，保存值将在下次启动时生效。",
                "Cetus · 端口设置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RetryDshAsync(recreateHost: true);
    }

    /// <summary>Close-to-tray unless an explicit exit has been requested.</summary>
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

    /// <summary>Real exit (tray → 退出): cancel work and stop the owned host asynchronously.</summary>
    private async void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        CancelStartup();
        CancelAutomaticRecovery();
        _tray?.Dispose();
        await StopHostAsync();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Last-resort cleanup when the process dies through other paths.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _isExiting = true;
        CancelStartup();
        CancelAutomaticRecovery();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
        _ = StopHostAsync();
        base.OnClosed(e);
    }
}
