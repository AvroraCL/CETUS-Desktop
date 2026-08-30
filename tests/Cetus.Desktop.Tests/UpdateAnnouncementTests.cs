using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class UpdateAnnouncementTests
{
    [Theory]
    [InlineData(null, "0.2.3", false)]
    [InlineData("0.2.3", "0.2.3", false)]
    [InlineData("0.2.3", "0.2.4", true)]
    [InlineData("v0.2.2", "0.2.10", true)]
    [InlineData("0.2.4", "0.2.3", false)]
    [InlineData("nonsense", "0.2.4", false)]
    [InlineData("0.2.2", "nonsense", false)]
    public void ShouldAnnounce_OnlyForVersionIncreases(
        string? lastLaunchVersion,
        string currentVersion,
        bool expected)
    {
        Assert.Equal(expected, UpdateAnnouncement.ShouldAnnounce(lastLaunchVersion, currentVersion));
    }

    [Theory]
    [InlineData("v0.2.3", "0.2.3")]
    [InlineData("0.2.30", "0.2.30")]
    [InlineData(" 0.1.9 ", "0.1.9")]
    public void NormalizeVersion_TrimsTagPrefixAndWhitespace(string? value, string expected)
    {
        Assert.Equal(expected, UpdateAnnouncement.NormalizeVersion(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release/beta")]
    public void NormalizeVersion_RejectsUnusableValues(string? value)
    {
        Assert.Null(UpdateAnnouncement.NormalizeVersion(value));
    }

    [Fact]
    public void BuildPageUrl_CarriesBothVersions()
    {
        string url = UpdateAnnouncement.BuildPageUrl("v0.2.2", "0.2.3");
        Assert.Equal(UpdateAnnouncement.PageBaseUrl + "?from=0.2.2&to=0.2.3", url);
    }

    [Fact]
    public void BuildPageUrl_OmitsMissingFromVersion()
    {
        string url = UpdateAnnouncement.BuildPageUrl(null, "v0.2.3");
        Assert.Equal(UpdateAnnouncement.PageBaseUrl + "?to=0.2.3", url);
    }

    [Fact]
    public void BuildPageUrl_FallsBackToBaseWithoutVersions()
    {
        Assert.Equal(UpdateAnnouncement.PageBaseUrl, UpdateAnnouncement.BuildPageUrl(null, null));
        Assert.Equal(UpdateAnnouncement.PageBaseUrl, UpdateAnnouncement.BuildPageUrl("junk", "junk"));
    }

    [Fact]
    public void PageBaseUrl_MatchesThePagesSite()
    {
        Assert.Equal("https://avroracl.github.io/CETUS-Desktop/", UpdateAnnouncement.PageBaseUrl);
    }
}
