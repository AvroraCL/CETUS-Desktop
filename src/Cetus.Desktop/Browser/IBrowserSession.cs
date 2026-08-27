namespace Cetus.Browser;

/// <summary>
/// Internal seam between runtime orchestration and the WebView2 implementation.
/// </summary>
internal interface IBrowserSession
{
    Task NavigateAsync(Uri trustedOrigin, CancellationToken cancellationToken);

    void Hide();
}
