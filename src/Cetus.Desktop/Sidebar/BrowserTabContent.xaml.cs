using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cetus.Configuration;
using Microsoft.Web.WebView2.Core;

namespace Cetus.Sidebar;

/// <summary>
/// One browser tab: its own WebView2 surface with the compact toolbar.
/// Instances share a single cached CoreWebView2Environment (and therefore a
/// single browser process) via the same user data folder.
/// </summary>
public partial class BrowserTabContent : UserControl, IDisposable
{
    private static Task<CoreWebView2Environment>? _environmentTask;

    private readonly string? _initialUrl;
    private bool _initialized;
    private bool _showingStartPage = true;
    private bool _isDark = true;
    private bool _disposed;

    /// <summary>Raised whenever the page document title changes.</summary>
    public event EventHandler<string>? TitleChanged;

    public BrowserTabContent(string? initialUrl)
    {
        InitializeComponent();
        _initialUrl = initialUrl;
        Loaded += (_, _) => _ = EnsureInitializedAsync();
    }

    /// <summary>The address to remember when the tab closes; null on the start page.</summary>
    public string? CurrentAddress =>
        _initialized && !_showingStartPage && TabWeb.Source is { } source && source.AbsoluteUri != "about:blank"
            ? source.AbsoluteUri
            : null;

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        TabWeb.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            255,
            isDark ? 27 : 245,
            isDark ? 27 : 247,
            isDark ? 28 : 250);
        if (_initialized && _showingStartPage)
        {
            ShowStartPage();
        }
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environmentTask is not null)
        {
            return _environmentTask;
        }

        string parent = Path.GetDirectoryName(CetusPaths.WebView2UserDataDirectory)
            ?? CetusPaths.UserDataDirectory;
        string userData = Path.Combine(parent, "SidebarWebView2");
        _environmentTask = CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userData);
        return _environmentTask;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        try
        {
            CoreWebView2Environment environment = await GetEnvironmentAsync();
            await TabWeb.EnsureCoreWebView2Async(environment);
            if (_disposed)
            {
                return;
            }

            CoreWebView2 core = TabWeb.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DocumentTitleChanged += OnDocumentTitleChanged;
            ApplyTheme(_isDark);
            if (_initialUrl is not null)
            {
                Navigate(new Uri(_initialUrl));
            }
            else
            {
                ShowStartPage();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            _initialized = false;
            StatusText.Text = $"浏览器初始化失败：{error.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri)
            || !SidebarBrowserAddress.IsAllowed(uri))
        {
            e.Cancel = true;
            return;
        }

        _showingStartPage = uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        BackButton.IsEnabled = TabWeb.CanGoBack;
        ForwardButton.IsEnabled = TabWeb.CanGoForward;
        if (_showingStartPage)
        {
            AddressBox.Clear();
        }
        else if (TabWeb.Source is { } source && source.AbsoluteUri != "about:blank")
        {
            AddressBox.Text = source.AbsoluteUri;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri)
            && SidebarBrowserAddress.IsAllowed(uri))
        {
            Navigate(uri);
        }
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        string title = string.IsNullOrWhiteSpace(TabWeb.CoreWebView2.DocumentTitle)
            ? SidebarTabModel.TitleOf(SidebarTabKind.Browser)
            : TabWeb.CoreWebView2.DocumentTitle;
        TitleChanged?.Invoke(this, title);
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (TabWeb.CanGoBack)
        {
            TabWeb.GoBack();
        }
    }

    private void OnForwardClicked(object sender, RoutedEventArgs e)
    {
        if (TabWeb.CanGoForward)
        {
            TabWeb.GoForward();
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) =>
        TabWeb.CoreWebView2?.Reload();

    private void OnGoClicked(object sender, RoutedEventArgs e) => NavigateAddress();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateAddress();
        }
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e) =>
        AddressPlaceholder.Visibility = AddressBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void NavigateAddress() =>
        Navigate(SidebarBrowserAddress.Resolve(AddressBox.Text));

    private void Navigate(Uri uri)
    {
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            ShowStartPage();
        }
        else if (TabWeb.CoreWebView2 is { } core)
        {
            _showingStartPage = false;
            core.Navigate(uri.AbsoluteUri);
        }
    }

    private void ShowStartPage()
    {
        if (TabWeb.CoreWebView2 is not { } core)
        {
            return;
        }

        _showingStartPage = true;
        AddressBox.Clear();
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized && TabWeb.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.DocumentTitleChanged -= OnDocumentTitleChanged;
        }

        TabWeb.Dispose();
    }
}
