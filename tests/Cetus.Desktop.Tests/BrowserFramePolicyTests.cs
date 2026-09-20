using Cetus.Browser;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// The embedded browser hosts the Harness shell and, inside it, the sidebar web
/// browser that the Harness renders as an iframe pointing at arbitrary sites.
/// Frame navigation therefore has to leave the DSH origin while still refusing
/// targets that reach the machine instead of the web.
/// </summary>
public sealed class BrowserFramePolicyTests
{
    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/page?q=1#top")]
    [InlineData("http://example.com/")]
    [InlineData("http://127.0.0.1:8080/preview")]
    [InlineData("https://localhost:3000/")]
    public void FrameNavigation_AllowsWebDocuments(string uri)
    {
        Assert.True(BrowserSession.IsWebDocument(uri));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("ms-settings:privacy")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("about:blank")]
    [InlineData("not a url")]
    [InlineData("")]
    public void FrameNavigation_RefusesNonWebTargets(string uri)
    {
        Assert.False(BrowserSession.IsWebDocument(uri));
    }

    /// <summary>
    /// The loopback policy still governs top-level navigation: the shell may
    /// only ever be replaced by the configured DSH origin.
    /// </summary>
    [Fact]
    public void TopLevelNavigation_StaysRestrictedToTheDshOrigin()
    {
        var policy = new LoopbackNavigationPolicy(new Uri("http://127.0.0.1:3080/"));

        Assert.True(policy.Allows("http://127.0.0.1:3080/"));
        Assert.True(policy.Allows("http://127.0.0.1:3080/session/abc"));
        Assert.False(policy.Allows("https://example.com/"));
        Assert.False(policy.Allows("http://127.0.0.1:3081/"));
        Assert.False(policy.Allows("http://localhost:3080/"));
        Assert.False(policy.Allows("file:///C:/Windows/win.ini"));
        Assert.False(policy.Allows("http://user:pass@127.0.0.1:3080/"));
    }
}
