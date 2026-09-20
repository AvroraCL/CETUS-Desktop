namespace Cetus.Browser;

/// <summary>
/// Internal seam between runtime orchestration and the WebView2 implementation.
/// </summary>
internal interface IBrowserSession
{
    Task NavigateAsync(Uri trustedOrigin, CancellationToken cancellationToken);

    void Hide();
}

/// <summary>
/// The one thing update orchestration needs from the browser: re-render the
/// in-page update notice. Kept separate so the update flow does not depend on
/// the whole WebView2 surface.
/// </summary>
internal interface IUpdateNoticeSink
{
    void PostUpdateState();
}
