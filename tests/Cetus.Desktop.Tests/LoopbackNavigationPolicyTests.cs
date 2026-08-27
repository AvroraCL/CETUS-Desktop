using Cetus.Browser;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class LoopbackNavigationPolicyTests
{
    private readonly LoopbackNavigationPolicy _policy =
        new(new Uri("http://127.0.0.1:3080/"));

    [Theory]
    [InlineData("http://127.0.0.1:3080/")]
    [InlineData("http://127.0.0.1:3080/chat?id=42")]
    [InlineData("HTTP://127.0.0.1:3080/settings")]
    public void Allows_PathsOnTheExactConfiguredOrigin(string uri) =>
        Assert.True(_policy.Allows(uri));

    [Theory]
    [InlineData("http://127.0.0.1:3081/")]
    [InlineData("https://127.0.0.1:3080/")]
    [InlineData("http://localhost:3080/")]
    [InlineData("http://user@127.0.0.1:3080/")]
    [InlineData("https://example.com/")]
    [InlineData("not a uri")]
    public void Allows_RejectsEveryOtherOriginAndMalformedInput(string uri) =>
        Assert.False(_policy.Allows(uri));

    [Fact]
    public void Constructor_RejectsNonLoopbackOrigin() =>
        Assert.Throws<ArgumentException>(() =>
            new LoopbackNavigationPolicy(new Uri("https://example.com/")));
}
