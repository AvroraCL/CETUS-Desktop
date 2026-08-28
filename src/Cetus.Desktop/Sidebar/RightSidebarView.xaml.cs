using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cetus.Configuration;
using Microsoft.Web.WebView2.Core;

namespace Cetus.Sidebar;

public partial class RightSidebarView : UserControl, IDisposable
{
    private const int MaximumTerminalCharacters = 250_000;

    private readonly SidebarTerminalSession _terminal = new();
    private bool _browserInitialized;
    private bool _browserShowingStartPage = true;
    private bool _isDark = true;
    private bool _terminalStarted;
    private bool _disposed;
    private string _filesRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public RightSidebarView()
    {
        InitializeComponent();
        _terminal.OutputReceived += OnTerminalOutputReceived;
        _terminal.Exited += OnTerminalExited;
        Loaded += OnLoaded;
    }

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        SidebarBrowser.DefaultBackgroundColor = isDark
            ? System.Drawing.Color.FromArgb(255, 27, 27, 28)
            : System.Drawing.Color.FromArgb(255, 245, 247, 250);

        if (_browserInitialized && _browserShowingStartPage)
        {
            ShowBrowserStartPage();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadFilesRoot(_filesRoot);
        await EnsureBrowserInitializedAsync();
    }

    private async void OnBrowserTabClicked(object sender, RoutedEventArgs e)
    {
        ShowPanel(BrowserPanel, BrowserTabButton);
        await EnsureBrowserInitializedAsync();
    }

    private void OnTerminalTabClicked(object sender, RoutedEventArgs e)
    {
        ShowPanel(TerminalPanel, TerminalTabButton);
        EnsureTerminalStarted();
        TerminalInput.Focus();
    }

    private void OnFilesTabClicked(object sender, RoutedEventArgs e) =>
        ShowPanel(FilesPanel, FilesTabButton);

    private void ShowPanel(UIElement panel, System.Windows.Controls.Primitives.ToggleButton selected)
    {
        BrowserPanel.Visibility = panel == BrowserPanel ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanel.Visibility = panel == TerminalPanel ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = panel == FilesPanel ? Visibility.Visible : Visibility.Collapsed;
        BrowserTabButton.IsChecked = selected == BrowserTabButton;
        TerminalTabButton.IsChecked = selected == TerminalTabButton;
        FilesTabButton.IsChecked = selected == FilesTabButton;
    }

    private async Task EnsureBrowserInitializedAsync()
    {
        if (_browserInitialized || _disposed)
        {
            return;
        }

        _browserInitialized = true;
        try
        {
            string parent = Path.GetDirectoryName(CetusPaths.WebView2UserDataDirectory)
                ?? CetusPaths.UserDataDirectory;
            string userData = Path.Combine(parent, "SidebarWebView2");
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData);
            await SidebarBrowser.EnsureCoreWebView2Async(environment);
            if (_disposed)
            {
                return;
            }

            CoreWebView2 core = SidebarBrowser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += OnBrowserNavigationStarting;
            core.NavigationCompleted += OnBrowserNavigationCompleted;
            core.NewWindowRequested += OnBrowserNewWindowRequested;
            core.DownloadStarting += OnBrowserDownloadStarting;
            ShowBrowserStartPage();
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            _browserInitialized = false;
            BrowserStatusText.Text = $"浏览器初始化失败：{error.Message}";
            BrowserStatusText.Visibility = Visibility.Visible;
        }
    }

    private void OnBrowserNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri)
            || !SidebarBrowserAddress.IsAllowed(uri))
        {
            e.Cancel = true;
            return;
        }

        _browserShowingStartPage = uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
    }

    private void OnBrowserNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        BrowserBackButton.IsEnabled = SidebarBrowser.CanGoBack;
        BrowserForwardButton.IsEnabled = SidebarBrowser.CanGoForward;
        if (_browserShowingStartPage)
        {
            BrowserAddressBox.Clear();
        }
        else if (SidebarBrowser.Source is { } source && source.AbsoluteUri != "about:blank")
        {
            BrowserAddressBox.Text = source.AbsoluteUri;
        }
    }

    private void OnBrowserNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri)
            && SidebarBrowserAddress.IsAllowed(uri))
        {
            NavigateBrowser(uri);
        }
    }

    private void OnBrowserDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        // The narrow sidebar is not a download manager; hand downloads to the
        // system browser instead of showing the in-view download overlay.
        e.Cancel = true;
        if (Uri.TryCreate(e.DownloadOperation.Uri, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // External launch is best effort; the sidebar download stays cancelled.
            }
        }
    }

    private void OnBrowserBackClicked(object sender, RoutedEventArgs e)
    {
        if (SidebarBrowser.CanGoBack)
        {
            SidebarBrowser.GoBack();
        }
    }

    private void OnBrowserForwardClicked(object sender, RoutedEventArgs e)
    {
        if (SidebarBrowser.CanGoForward)
        {
            SidebarBrowser.GoForward();
        }
    }

    private void OnBrowserRefreshClicked(object sender, RoutedEventArgs e) =>
        SidebarBrowser.CoreWebView2?.Reload();

    private void OnBrowserGoClicked(object sender, RoutedEventArgs e) => NavigateBrowserInput();

    private void OnBrowserAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateBrowserInput();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            BrowserAddressBox.Clear();
        }
    }

    private void OnSidebarPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L
            && Keyboard.Modifiers == ModifierKeys.Control
            && BrowserPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            FocusBrowserAddress();
        }
    }

    private void OnBrowserAddressGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        BrowserAddressBox.SelectAll();

    private void OnBrowserAddressPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!BrowserAddressBox.IsKeyboardFocusWithin)
        {
            FocusBrowserAddress();
            e.Handled = true;
        }
    }

    private void FocusBrowserAddress()
    {
        BrowserAddressBox.Focus();
        BrowserAddressBox.SelectAll();
    }

    private void NavigateBrowserInput() =>
        NavigateBrowser(SidebarBrowserAddress.Resolve(BrowserAddressBox.Text));

    private void NavigateBrowser(Uri uri)
    {
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            ShowBrowserStartPage();
        }
        else if (SidebarBrowser.CoreWebView2 is { } core)
        {
            _browserShowingStartPage = false;
            core.Navigate(uri.AbsoluteUri);
        }
    }

    private void ShowBrowserStartPage()
    {
        if (SidebarBrowser.CoreWebView2 is not { } core)
        {
            return;
        }

        _browserShowingStartPage = true;
        BrowserAddressBox.Clear();
        string background = _isDark ? "#1b1b1c" : "#f5f7fa";
        string foreground = _isDark ? "#8b8b90" : "#667085";
        string html = $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <style>
                html, body { width: 100%; height: 100%; margin: 0; }
                body {
                  display: grid;
                  place-items: center;
                  background: {{background}};
                  color: {{foreground}};
                  font: 13px "Segoe UI", "Microsoft YaHei", sans-serif;
                }
              </style>
            </head>
            <body>输入网址或搜索内容</body>
            </html>
            """;
        core.NavigateToString(html);
    }

    private void EnsureTerminalStarted()
    {
        if (_terminalStarted || _disposed)
        {
            return;
        }

        try
        {
            _terminal.Start();
            _terminalStarted = true;
            AppendTerminalLine("CETUS PowerShell · 输入命令后按 Enter", isError: false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppendTerminalLine($"终端启动失败：{error.Message}", isError: true);
        }
    }

    private void OnTerminalRunClicked(object sender, RoutedEventArgs e) => SendTerminalCommand();

    private void OnTerminalInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendTerminalCommand();
        }
    }

    private void SendTerminalCommand()
    {
        EnsureTerminalStarted();
        string command = TerminalInput.Text;
        TerminalInput.Clear();
        try
        {
            _terminal.SendCommand(command);
        }
        catch (InvalidOperationException error)
        {
            AppendTerminalLine(error.Message, isError: true);
        }
    }

    private void OnTerminalOutputReceived(string line, bool isError) =>
        _ = Dispatcher.InvokeAsync(() => AppendTerminalLine(line, isError));

    private void OnTerminalExited() =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            _terminalStarted = false;
            AppendTerminalLine("PowerShell 已退出。", isError: true);
        });

    private void AppendTerminalLine(string line, bool isError)
    {
        if (_disposed)
        {
            return;
        }

        if (TerminalOutput.Text.Length > MaximumTerminalCharacters)
        {
            TerminalOutput.Text = TerminalOutput.Text[^150_000..];
        }

        if (TerminalOutput.Text.Length > 0)
        {
            TerminalOutput.AppendText(Environment.NewLine);
        }

        TerminalOutput.AppendText(isError ? $"! {line}" : line);
        TerminalOutput.ScrollToEnd();
    }

    private void OnChooseFolderClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择右侧文件面板的根目录",
            InitialDirectory = _filesRoot,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            LoadFilesRoot(dialog.SelectedPath);
        }
    }

    private void OnFilesRefreshClicked(object sender, RoutedEventArgs e) => LoadFilesRoot(_filesRoot);

    private void LoadFilesRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        _filesRoot = path;
        FilesPathBox.Text = path;
        var root = new SidebarFileNode(path, isDirectory: true);
        root.LoadChildren();
        FilesTree.ItemsSource = new[] { root };
    }

    private void OnFileNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: SidebarFileNode node })
        {
            node.LoadChildren();
        }
    }

    private void OnFileNodeDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FilesTree, e.OriginalSource as DependencyObject)
            is not TreeViewItem { DataContext: SidebarFileNode { IsDirectory: false } node })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _ = MessageBox.Show(
                Window.GetWindow(this),
                $"无法打开文件：{error.Message}",
                "CETUS · 文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;
        _terminal.OutputReceived -= OnTerminalOutputReceived;
        _terminal.Exited -= OnTerminalExited;
        _terminal.Dispose();
        if (_browserInitialized && SidebarBrowser.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnBrowserNavigationStarting;
            core.NavigationCompleted -= OnBrowserNavigationCompleted;
            core.NewWindowRequested -= OnBrowserNewWindowRequested;
            core.DownloadStarting -= OnBrowserDownloadStarting;
        }
        SidebarBrowser.Dispose();
    }
}
