using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Cetus.Desktop.Core;
using Microsoft.Web.WebView2.Core;

namespace Cetus.Desktop;

public partial class MainWindow : Window
{
    private readonly CetusConfig _config = CetusConfig.Load();
    private DshServerProcess? _server;

    public MainWindow()
    {
        InitializeComponent();
        Browser.CoreWebView2InitializationCompleted += OnWebView2Ready;
        Browser.NavigationCompleted += OnNavigationCompleted;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Splash.Visibility = Visibility.Visible;
        FailurePanel.Visibility = Visibility.Collapsed;
        SplashStatus.Foreground = System.Windows.Media.Brushes.Gray;
        SplashStatus.Text = "正在启动 DeepSeek Harness 后端…";
        CetusTrace.Info($"window loaded, target={_config.Url}");

        try
        {
            _server = new DshServerProcess(_config);

            // Reuse a healthy server, otherwise spawn node hidden and wait up to 60s.
            if (!await _server.EnsureReadyAsync())
            {
                ShowFailure($"后端在 {_config.ReadyTimeout.TotalSeconds:0} 秒内未就绪。" +
                            (_server.LogPath is { } log ? $"\n日志：{log}" : ""));
                return;
            }

            CetusTrace.Info(_server.SpawnedByUs
                ? $"backend spawned, pid={_server.ProcessId}, log={_server.LogPath}"
                : "backend reused (healthy server already running)");

            SplashStatus.Text = _server.SpawnedByUs
                ? $"后端已启动（PID {_server.ProcessId}），正在加载界面…"
                : "检测到已在运行的后端，正在加载界面…";

            // Isolated WebView2 profile (cache, cookies, local storage) so the
            // shell never mixes state with other WebView2 apps.
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: CetusPaths.WebView2DataDir);

            await Browser.EnsureCoreWebView2Async(environment);
            CetusTrace.Info("webview2 initialized");
            Browser.Source = _config.Url;
            CetusTrace.Info($"navigating to {_config.Url}");
        }
        catch (Exception ex)
        {
            CetusTrace.Info($"startup failed: {ex}");
            ShowFailure(ex.Message);
        }
    }

    private void OnWebView2Ready(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess) return;
        var message = $"WebView2 初始化失败：{e.InitializationException?.Message}";
        CetusTrace.Info(message);
        Dispatcher.Invoke(() => ShowFailure(message));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // NavigationCompleted args carry no URI; Browser.Source is the current
        // page after the navigation settles (redirect target included).
        var isTarget = Uri.TryCreate(Browser.Source.ToString(), UriKind.Absolute, out var uri)
                       && uri.Host == _config.Url.Host
                       && uri.Port == _config.Url.Port;
        CetusTrace.Info($"nav completed: success={e.IsSuccess} source={Browser.Source} isTarget={isTarget}");
        if (isTarget && e.IsSuccess)
        {
            CetusTrace.Info("page loaded, hiding splash");
            Splash.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowFailure(string message)
    {
        SplashStatus.Text = $"启动失败：{message}";
        SplashStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
        Splash.Visibility = Visibility.Visible;
        FailurePanel.Visibility = Visibility.Visible;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
        => OnWindowLoaded(sender, e);

    private void OnQuitClick(object sender, RoutedEventArgs e)
        => Close();

    // M0: closing the window exits the app and recycles the sidecar we spawned.
    // (Tray-minimize behavior arrives in M1.)
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            Browser.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cetus: dispose WebView2 failed: {ex.Message}");
        }
        _server?.Stop();
    }
}
