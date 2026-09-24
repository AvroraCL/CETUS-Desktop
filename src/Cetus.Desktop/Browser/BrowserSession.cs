using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
internal sealed class BrowserSession : IBrowserSession, IUpdateNoticeSink, IDisposable
{
    private const string WindowBridgeSource = "cetus-window";

    /// <summary>
    /// The WebView2 bridge script, kept in BridgeScript.js so tests/bridge can
    /// execute exactly these bytes against a DOM shim.
    /// </summary>
    private static readonly string WindowBridgeScript = BridgeScript.Source;

    private readonly WebView2 _view;
    private readonly Action<bool> _themeChanged;
    private readonly Func<IReadOnlyDictionary<string, string>>? _cetusSettingsProvider;
    private readonly Action<string, string>? _cetusSettingChanged;
    private readonly Action? _openPortSettings;
    private readonly Action? _checkForUpdates;
    private readonly Action? _checkDshUpdate;
    private readonly Action? _installUpdate;
    private readonly Action? _openReleasePage;
    private readonly Action? _dismissUpdate;
    private readonly Func<string?>? _updateStateProvider;
    private LoopbackNavigationPolicy? _navigationPolicy;
    private CoreWebView2Environment? _environment;
    private HttpClient? _frameFetch;
    private const int MaxFrameDocumentBytes = 32 * 1024 * 1024;
    private bool _initialized;
    private bool _disposed;

    public BrowserSession(
        WebView2 view,
        Action<bool> themeChanged,
        Func<IReadOnlyDictionary<string, string>>? cetusSettingsProvider = null,
        Action<string, string>? cetusSettingChanged = null,
        Action? openPortSettings = null,
        Action? checkForUpdates = null,
        Action? checkDshUpdate = null,
        Action? installUpdate = null,
        Action? openReleasePage = null,
        Action? dismissUpdate = null,
        Func<string?>? updateStateProvider = null)
    {
        _view = view;
        _themeChanged = themeChanged;
        _cetusSettingsProvider = cetusSettingsProvider;
        _cetusSettingChanged = cetusSettingChanged;
        _openPortSettings = openPortSettings;
        _checkForUpdates = checkForUpdates;
        _checkDshUpdate = checkDshUpdate;
        _installUpdate = installUpdate;
        _openReleasePage = openReleasePage;
        _dismissUpdate = dismissUpdate;
        _updateStateProvider = updateStateProvider;
    }

    /// <summary>
    /// Same resolution as CetusSettings.DshHomeOverride; read here so the
    /// browser signs its cookie against the same DSH home the probe uses.
    /// </summary>
    private static string? ResolveDshHomeOverride()
    {
        string? value = Environment.GetEnvironmentVariable("CETUS_DSH_HOME");
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
            _environment = environment;
            await _view.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();

            CoreWebView2 core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += OnTopLevelNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            // Serve framed web documents without the embed-blocking headers
            // (X-Frame-Options / CSP frame-ancestors) so the Harness sidebar
            // browser can render sites like GitHub that refuse iframing.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += OnWebResourceRequested;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WindowBridgeScript);
            _initialized = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // The override matters: with CETUS_DSH_HOME set, the probe and the
        // session clients sign against that home, so the browser cookie must
        // come from the same place or the page loads a 401 while the probe
        // happily reports the host as healthy.
        if (Cetus.Hosting.DshAuth.TryGetSessionCookie(trustedOrigin, ResolveDshHomeOverride()) is { } cookie)
        {
            var cookieObj = _view.CoreWebView2.CookieManager.CreateCookie(
                cookie.Name,
                cookie.Value,
                trustedOrigin.Host,
                "/");
            cookieObj.IsHttpOnly = true;
            cookieObj.SameSite = CoreWebView2CookieSameSiteKind.Strict;
            _view.CoreWebView2.CookieManager.AddOrUpdateCookie(cookieObj);
        }

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

    /// <summary>Runs JavaScript in the current DSH page (trusted loopback content only).</summary>
    public async Task ExecuteScriptAsync(string script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("浏览器会话尚未初始化。");
        }

        // Bounded so a wedged page cannot hang workspace focus indefinitely.
        await _view.CoreWebView2.ExecuteScriptAsync(script)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Whether the CoreWebView2 environment exists and the bridge is wired.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Requests renderer suspension for a hidden window to cut memory use.
    /// Silent no-op while visible or when WebView2 refuses (e.g. audio).
    /// </summary>
    public async Task TrySuspendAsync()
    {
        if (!_initialized || _disposed || _view.Visibility == Visibility.Visible)
        {
            return;
        }

        try
        {
            await _view.CoreWebView2.TrySuspendAsync();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            // Suspension is opportunistic; resume-on-show keeps this safe.
        }
    }

    /// <summary>Wakes a suspended renderer (also happens automatically on visibility).</summary>
    public void Resume()
    {
        if (!_initialized || _disposed)
        {
            return;
        }

        try
        {
            _view.CoreWebView2.Resume();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
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
        Cetus.Platform.SystemBrowser.Open(e.Uri);
    }

    /// <summary>
    /// Frames are allowed to leave the DSH origin on purpose: the Harness ships
    /// a sidebar web browser built as an iframe pointing at arbitrary sites, so
    /// canceling every non-loopback frame navigation made that panel unusable.
    /// What still gets canceled is anything that is not a normal web document —
    /// file:, javascript:, data: and external protocol handlers — because those
    /// reach the machine rather than the web. Top-level navigation stays bound
    /// to the configured DSH origin by <see cref="OnTopLevelNavigationStarting"/>.
    /// </summary>
    internal static void OnFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsWebDocument(e.Uri))
        {
            return;
        }

        e.Cancel = true;
    }

    /// <summary>True for http/https URLs: the only frame targets Cetus permits.</summary>
    internal static bool IsWebDocument(string uriText) =>
        Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Serves framed web documents fetched by Cetus with the embed-blocking
    /// headers (X-Frame-Options, CSP frame-ancestors) stripped, so the
    /// Harness sidebar browser can render sites like GitHub that refuse
    /// iframing. DSH-origin documents (the Harness page itself and its
    /// reloads) always pass through untouched, as do non-GET requests.
    /// </summary>
    private void OnWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (e.ResourceContext != CoreWebView2WebResourceContext.Document
            || e.Request.Method != "GET"
            || !IsWebDocument(e.Request.Uri)
            || _navigationPolicy?.Allows(e.Request.Uri) == true)
        {
            return;
        }

        var deferral = e.GetDeferral();
        _ = ServeStrippedAsync(e, e.Request.Uri, deferral);
    }

    private async Task ServeStrippedAsync(
        CoreWebView2WebResourceRequestedEventArgs e,
        string uriText,
        CoreWebView2Deferral deferral)
    {
        try
        {
            if (_navigationPolicy?.Allows(uriText) == true)
            {
                return; // DSH origin: pass through untouched
            }

            _frameFetch ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using HttpResponseMessage upstream =
                await _frameFetch.GetAsync(uriText, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var headers = new System.Text.StringBuilder();
            foreach (var header in upstream.Headers)
            {
                if (EmbedBlockers.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in header.Value)
                {
                    headers.Append(header.Key).Append(": ").Append(value).Append('\n');
                }
            }

            foreach (var header in upstream.Content.Headers)
            {
                if (EmbedBlockers.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in header.Value)
                {
                    headers.Append(header.Key).Append(": ").Append(value).Append('\n');
                }
            }

            await using Stream source = await upstream.Content.ReadAsStreamAsync(timeout.Token);
            MemoryStream? body = await BufferFrameDocumentAsync(
                source, upstream.Content.Headers.ContentLength, timeout.Token);
            if (_disposed || _environment is null)
            {
                body?.Dispose();
                return;
            }

            // WebView2 receives an independent, fully buffered stream. The
            // upstream response can be disposed now instead of being pinned
            // for the entire lifetime of the window.
            e.Response = body is null
                ? _environment.CreateWebResourceResponse(Stream.Null, 413, "Payload Too Large", string.Empty)
                : _environment.CreateWebResourceResponse(
                    body, (int)upstream.StatusCode, upstream.ReasonPhrase, headers.ToString());
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            or OperationCanceledException or ObjectDisposedException)
        {
            // Let WebView2 render its own network error for the frame.
            _ = error;
        }
        finally
        {
            deferral.Complete();
        }
    }

    internal static async Task<MemoryStream?> BufferFrameDocumentAsync(
        Stream source,
        long? declaredLength,
        CancellationToken cancellationToken)
    {
        if (declaredLength > MaxFrameDocumentBytes)
        {
            return null;
        }

        var body = new MemoryStream();
        try
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length + read > MaxFrameDocumentBytes)
                {
                    body.Dispose();
                    return null;
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            body.Position = 0;
            return body;
        }
        catch
        {
            body.Dispose();
            throw;
        }
    }

    /// <summary>Header names whose presence prevents iframe embedding.</summary>
    internal static readonly HashSet<string> EmbedBlockers = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Frame-Options",
        "Content-Security-Policy",
    };


    private static void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Cetus.Platform.SystemBrowser.Open(e.Uri);
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            PostCetusSettingsState();
            PostUpdateState();
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The injected bridge lives only in the DSH page. Arbitrary third-
        // party sites load in iframes (Harness sidebar browser) and can post
        // messages with any payload — including a spoofed source field — so
        // the sender's ORIGIN must be the trusted DSH origin, not just the
        // message body.
        if (e.Source is not { } messageSource
            || _navigationPolicy?.Allows(messageSource) != true)
        {
            return;
        }

        try
        {
            using JsonDocument message = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("source", out JsonElement source)
                && source.GetString() == WindowBridgeSource
                && root.TryGetProperty("type", out JsonElement type))
            {
                if (type.GetString() == "theme"
                    && root.TryGetProperty("mode", out JsonElement mode))
                {
                    _themeChanged(mode.GetString() == "dark");
                }
                else if (type.GetString() == "cetus-settings-request")
                {
                    PostCetusSettingsState();
                }
                else if (type.GetString() == "cetus-setting-changed"
                    && root.TryGetProperty("key", out JsonElement key)
                    && root.TryGetProperty("value", out JsonElement value))
                {
                    if (_cetusSettingChanged is not null)
                    {
                        string settingKey = key.GetString() ?? string.Empty;
                        // The bridge always posts values as strings.
                        _cetusSettingChanged(settingKey, value.ToString());
                        PostCetusSettingsState();
                    }
                }
                else if (type.GetString() == "cetus-open-port-settings")
                {
                    _openPortSettings?.Invoke();
                }
                else if (type.GetString() == "cetus-check-dsh-update")
                {
                    _checkDshUpdate?.Invoke();
                }
                else if (type.GetString() == "cetus-check-updates")
                {
                    _checkForUpdates?.Invoke();
                }
                else if (type.GetString() == "cetus-update-state-request")
                {
                    PostUpdateState();
                }
                else if (type.GetString() == "cetus-update-install")
                {
                    _installUpdate?.Invoke();
                }
                else if (type.GetString() == "cetus-update-details")
                {
                    _openReleasePage?.Invoke();
                }
                else if (type.GetString() == "cetus-update-dismiss")
                {
                    _dismissUpdate?.Invoke();
                }
            }
        }
        catch (JsonException)
        {
            // Ignore messages not emitted by the document-created bridge.
        }
    }

    /// <summary>Pushes the current CETUS settings into the injected settings group.</summary>
    public void PostCetusSettingsState()
    {
        if (!_initialized || _disposed || _view.CoreWebView2 is not { } core)
        {
            return;
        }

        IReadOnlyDictionary<string, string> values = _cetusSettingsProvider?.Invoke()
            ?? new Dictionary<string, string>();
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            source = WindowBridgeSource,
            type = "cetus-settings-state",
            values,
        }));
    }

    /// <summary>
    /// Pushes the current update state so the in-page notice can render. The
    /// payload is produced by the update coordinator, which owns the meaning of
    /// every field; when no coordinator exists or the page is not ready yet the
    /// call is remembered and replayed after the next navigation.
    /// </summary>
    public void PostUpdateState()
    {
        if (_updateStateProvider is null)
        {
            return;
        }

        if (!_initialized || _disposed || _view.CoreWebView2 is not { } core)
        {
            // The page will ask for the state again once it loads.
            return;
        }

        string json = _updateStateProvider() ?? EmptyUpdateState;
        using JsonDocument update = JsonDocument.Parse(json);
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            source = WindowBridgeSource,
            type = "cetus-update-state",
            update = update.RootElement.Clone(),
        }));
    }

    private const string EmptyUpdateState = """{"available":false}""";

    private bool IsTrusted(string uriText)
    {
        return _navigationPolicy?.Allows(uriText) == true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.Visibility = Visibility.Collapsed;
        _view.Dispose();
        _frameFetch?.Dispose();
    }
}
