using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Cetus.Configuration;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Cetus.Browser;

/// <summary>
/// Owns the complete WebView2 session: environment initialization, trusted
/// origin enforcement, external-link delegation and the Harness theme bridge.
/// </summary>
internal sealed class BrowserSession : IBrowserSession, IDisposable
{
    private const string ThemeBridgeSource = "cetus-window";

    private static readonly string ThemeBridgeScript = """
        (() => {
          const source = 'cetus-window';
          const report = () => {
            const root = document.documentElement;
            if (!root || !window.chrome || !window.chrome.webview) return;
            const classes = [
              root.className,
              root.getAttribute('data-theme'),
              document.body && document.body.className,
              document.body && document.body.getAttribute('data-theme')
            ].filter(Boolean).join(' ').toLowerCase();
            const scheme = getComputedStyle(root).colorScheme.toLowerCase();
            const dark = /(^|[^a-z])(dark|night)(?=$|[^a-z])/.test(classes)
              || (!/(^|[^a-z])(light|day)(?=$|[^a-z])/.test(classes)
                  && (scheme.includes('dark') || window.matchMedia('(prefers-color-scheme: dark)').matches));
            window.chrome.webview.postMessage({ source, type: 'theme', mode: dark ? 'dark' : 'light' });
          };
          const install = () => {
            const root = document.documentElement;
            if (!root) return;
            const observer = new MutationObserver(report);
            observer.observe(root, { attributes: true, attributeFilter: ['class', 'data-theme', 'style'] });
            if (document.body) {
              observer.observe(document.body, { attributes: true, attributeFilter: ['class', 'data-theme', 'style'] });
            }
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', report);
            report();
          };
          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', install, { once: true });
          } else {
            install();
          }
        })();
        """;

    private readonly WebView2 _view;
    private readonly Action<bool> _themeChanged;
    private LoopbackNavigationPolicy? _navigationPolicy;
    private bool _initialized;
    private bool _disposed;

    public BrowserSession(WebView2 view, Action<bool> themeChanged)
    {
        _view = view;
        _themeChanged = themeChanged;
    }

    public async Task NavigateAsync(Uri trustedOrigin, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(trustedOrigin);
        _navigationPolicy = new LoopbackNavigationPolicy(trustedOrigin);
        if (!_initialized)
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: CetusPaths.WebView2UserDataDirectory);
            await _view.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();

            CoreWebView2 core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += OnTopLevelNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ThemeBridgeScript);
            _initialized = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _view.CoreWebView2.Navigate(trustedOrigin.AbsoluteUri);
        _view.Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        if (!_disposed)
        {
            _view.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTopLevelNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsTrusted(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenInSystemBrowser(e.Uri);
    }

    private void OnFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsTrusted(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenInSystemBrowser(e.Uri);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument message = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("source", out JsonElement source)
                && root.TryGetProperty("type", out JsonElement type)
                && root.TryGetProperty("mode", out JsonElement mode)
                && source.GetString() == ThemeBridgeSource
                && type.GetString() == "theme")
            {
                _themeChanged(mode.GetString() == "dark");
            }
        }
        catch (JsonException)
        {
            // Ignore messages not emitted by the document-created bridge.
        }
    }

    private bool IsTrusted(string uriText)
    {
        return _navigationPolicy?.Allows(uriText) == true;
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
            // External launch is best effort; in-app navigation remains blocked.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized && _view.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnTopLevelNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
        }
    }
}
