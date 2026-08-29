using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    private const string ElementPickerScript = """
        (() => new Promise((resolve) => {
          if (window.__cetusPicking) { resolve(''); return; }
          window.__cetusPicking = true;
          const overlay = document.createElement('div');
          overlay.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;border:2px solid #4f8ef7;border-radius:4px;background:rgba(79,142,247,.12)';
          document.documentElement.appendChild(overlay);
          const cleanup = () => {
            window.__cetusPicking = false;
            overlay.remove();
            document.removeEventListener('mousemove', onMove, true);
            document.removeEventListener('click', onClick, true);
            document.removeEventListener('keydown', onKey, true);
          };
          const onMove = (event) => {
            const target = event.target;
            if (!(target instanceof Element)) return;
            const rect = target.getBoundingClientRect();
            overlay.style.left = rect.left + 'px';
            overlay.style.top = rect.top + 'px';
            overlay.style.width = rect.width + 'px';
            overlay.style.height = rect.height + 'px';
            overlay.__cetusTarget = target;
          };
          const onKey = (event) => {
            if (event.key === 'Escape') { event.preventDefault(); cleanup(); resolve(''); }
          };
          const onClick = (event) => {
            event.preventDefault();
            event.stopPropagation();
            const target = event.target;
            cleanup();
            if (!(target instanceof Element)) { resolve(''); return; }
            const text = (target.innerText || '').trim().replace(/\s+/g, ' ').slice(0, 300);
            const html = target.outerHTML.slice(0, 800);
            const classes = target instanceof SVGElement
              ? (target.getAttribute('class') || '')
              : (target.className || '').toString();
            resolve(JSON.stringify({
              tag: target.tagName.toLowerCase(),
              id: target.id || '',
              cls: classes.slice(0, 120),
              text: text,
              html: html
            }));
          };
          document.addEventListener('mousemove', onMove, true);
          document.addEventListener('click', onClick, true);
          document.addEventListener('keydown', onKey, true);
        }))()
        """;

    private static Task<CoreWebView2Environment>? _environmentTask;

    private readonly string? _initialUrl;
    private bool _initialized;
    private bool _showingStartPage = true;
    private bool _isDark = true;
    private bool _picking;
    private bool _disposed;

    /// <summary>Raised whenever the page document title changes.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>
    /// Receives the picked element summary; true means it landed in the chat
    /// composer, false makes the tab fall back to the clipboard.
    /// </summary>
    public Func<string, Task<bool>>? ChatInserter { get; set; }

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

    private async void OnPickElementClicked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _disposed || TabWeb.CoreWebView2 is not { } core)
        {
            return;
        }

        if (_showingStartPage)
        {
            ShowStatus("先打开一个网页，再选择元素。");
            return;
        }

        if (_picking)
        {
            ShowStatus("正在选择元素：点击页面元素，或按 Esc 取消。");
            return;
        }

        _picking = true;
        PickElementButton.Opacity = 0.55;
        try
        {
            string raw = await core.ExecuteScriptAsync(ElementPickerScript);
            string summary = FormatPickedElement(raw);
            if (summary.Length == 0)
            {
                ShowStatus("已取消选择元素。");
                return;
            }

            bool inserted = ChatInserter is { } insert && await insert(summary);
            if (inserted)
            {
                ShowStatus("已将网页元素加入聊天输入框。");
            }
            else
            {
                Clipboard.SetText(summary);
                ShowStatus("聊天暂不可用，网页元素已复制到剪贴板。");
            }
        }
        catch (Exception error)
        {
            ShowStatus($"选择元素失败：{error.Message}");
        }
        finally
        {
            _picking = false;
            PickElementButton.Opacity = 1;
        }
    }

    /// <summary>Turns the picker's JSON reply into a chat-ready markdown block.</summary>
    private static string FormatPickedElement(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return string.Empty;
        }

        using JsonDocument document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        string payload = document.RootElement.GetString() ?? string.Empty;
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        using JsonDocument element = JsonDocument.Parse(payload);
        JsonElement root = element.RootElement;
        string tag = root.TryGetProperty("tag", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        string id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        string classes = root.TryGetProperty("cls", out var classElement) ? classElement.GetString() ?? string.Empty : string.Empty;
        string text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        string html = root.TryGetProperty("html", out var htmlElement) ? htmlElement.GetString() ?? string.Empty : string.Empty;
        if (tag.Length == 0)
        {
            return string.Empty;
        }

        var summary = new StringBuilder();
        summary.Append("[网页元素] <").Append(tag);
        if (id.Length > 0)
        {
            summary.Append(" id=\"").Append(id).Append('"');
        }

        if (classes.Length > 0)
        {
            summary.Append(" class=\"").Append(classes).Append('"');
        }

        summary.Append('>').AppendLine();
        if (text.Length > 0)
        {
            summary.AppendLine(text).AppendLine();
        }

        if (html.Length > 0)
        {
            summary.AppendLine("```html");
            summary.AppendLine(html);
            summary.Append("```");
        }

        return summary.ToString();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

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
        ChatInserter = null;
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
