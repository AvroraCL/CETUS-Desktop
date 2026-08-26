using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Cetus.Hosting;
using WpfMessageBox = System.Windows.MessageBox;

namespace Cetus;

/// <summary>
/// Main window: owns the DSH host lifecycle, the WebView2 surface, and the tray icon.
/// Closing the window hides to tray; the tray "退出" kills the host and exits.
/// </summary>
public partial class MainWindow : Window
{
    private const string DshUrl = "http://127.0.0.1:3080/";

    private readonly DshHost _host;
    private NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        _host = new DshHost(DshLocator.Resolve(), DshUrl);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            StatusText.Text = "正在启动 DSH 主机…";
            await _host.StartAsync();

            StatusText.Text = "正在加载界面…";
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Navigate(DshUrl);

            Browser.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            StatusText.Text = "启动失败";
            _ = WpfMessageBox.Show(
                this,
                error.Message,
                "Cetus · 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetupTray()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Cetus · 鲸鱼座",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => ShowWindow());
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

    /// <summary>Close-to-tray: cancel the close and hide instead of exiting.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        base.OnClosing(e);
    }

    /// <summary>Real exit (tray → 退出): release the tray and stop the host.</summary>
    private void ExitApplication()
    {
        _tray?.Dispose();
        _host.Stop();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Last-resort cleanup when the process dies through other paths.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _host.Stop();
        base.OnClosed(e);
    }
}
