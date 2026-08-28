using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class SidebarBrowserAddressTests
{
    [Theory]
    [InlineData("https://example.com/path", "https://example.com/path")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("localhost:4173", "http://localhost:4173/")]
    [InlineData("127.0.0.1:3084", "http://127.0.0.1:3084/")]
    [InlineData("127.0.0.1", "http://127.0.0.1/")]
    [InlineData("[::1]:3080", "http://[::1]:3080/")]
    public void Resolve_RecognizesAddresses(string input, string expected)
    {
        Assert.Equal(expected, SidebarBrowserAddress.Resolve(input).AbsoluteUri);
    }

    [Fact]
    public void Resolve_TreatsLookalikeLoopbackHostAsRemoteAddress()
    {
        Uri result = SidebarBrowserAddress.Resolve("127.0.0.1.evil.com");

        Assert.Equal("https://127.0.0.1.evil.com/", result.AbsoluteUri);
    }

    [Fact]
    public void Resolve_TreatsPlainTextAsSearch()
    {
        Uri result = SidebarBrowserAddress.Resolve("CETUS desktop");

        Assert.Equal("https://www.bing.com/search?q=CETUS%20desktop", result.AbsoluteUri);
    }

    [Fact]
    public void IsAllowed_RejectsLocalFiles()
    {
        Assert.False(SidebarBrowserAddress.IsAllowed(new Uri("file:///C:/secret.txt")));
    }
}
