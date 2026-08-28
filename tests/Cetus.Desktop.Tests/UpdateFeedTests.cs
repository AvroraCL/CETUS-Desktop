using System.Security.Cryptography;
using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class UpdateFeedTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v0.2.0",
          "name": "CETUS 0.2.0",
          "body": "- 自动更新",
          "assets": [
            {"name": "Cetus-Setup-0.2.0.exe", "browser_download_url": "https://example.com/Cetus-Setup-0.2.0.exe", "size": 1024},
            {"name": "Cetus-0.2.0-win-x64-portable.zip", "browser_download_url": "https://example.com/portable.zip", "size": 2048},
            {"name": "SHA256SUMS.txt", "browser_download_url": "https://example.com/SHA256SUMS.txt", "size": 128}
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsTagNotesAndAssets()
    {
        ReleaseInfo? release = UpdateFeed.Parse(ReleaseJson);

        Assert.NotNull(release);
        Assert.Equal("v0.2.0", release!.TagName);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("- 自动更新", release.Notes);
        Assert.Equal(3, release.Assets.Count);
        Assert.All(release.Assets, asset => Assert.False(string.IsNullOrWhiteSpace(asset.DownloadUrl)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"not-a-version"}""")]
    [InlineData("""{"tag_name":42}""")]
    public void Parse_ReturnsNullForUnusablePayloads(string json)
    {
        Assert.Null(UpdateFeed.Parse(json));
    }

    [Theory]
    [InlineData("v0.1.9", "0.1.9")]
    [InlineData("V0.2.10", "0.2.10")]
    [InlineData("0.3.0", "0.3.0")]
    public void TryParseTag_AcceptsVersionTags(string tag, string expected)
    {
        Assert.True(UpdateFeed.TryParseTag(tag, out Version version));
        Assert.Equal(new Version(expected), version);
    }

    [Theory]
    [InlineData("whale")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseTag_RejectsInvalidTags(string? tag)
    {
        Assert.False(UpdateFeed.TryParseTag(tag, out _));
    }

    [Fact]
    public void VersionComparison_TreatsPatchBumpsAsNewer()
    {
        UpdateFeed.TryParseTag("v0.1.9", out Version current);
        UpdateFeed.TryParseTag("v0.1.10", out Version next);

        Assert.True(next > current);
    }

    [Fact]
    public void SelectAssets_FindInstallerAndChecksums()
    {
        ReleaseInfo release = UpdateFeed.Parse(ReleaseJson)!;

        Assert.Equal("Cetus-Setup-0.2.0.exe", UpdateFeed.SelectInstallerAsset(release)?.Name);
        Assert.Equal("SHA256SUMS.txt", UpdateFeed.SelectChecksumAsset(release)?.Name);
    }

    [Fact]
    public void SelectInstallerAsset_ReturnsNullWithoutInstaller()
    {
        var release = new ReleaseInfo(
            "v0.2.0",
            new Version(0, 2, 0),
            null,
            [new ReleaseAsset("SHA256SUMS.txt", "https://example.com/SHA256SUMS.txt", 128)]);

        Assert.Null(UpdateFeed.SelectInstallerAsset(release));
    }

    [Fact]
    public void FindChecksum_MatchesNamesCaseInsensitively()
    {
        string hash = HashOf("installer-bytes");
        string sums = $"{hash}  Cetus-Setup-0.2.0.exe\r\n{HashOf("other")}  other.zip\n";

        Assert.Equal(hash, UpdateFeed.FindChecksum(sums, "cetus-setup-0.2.0.exe"));
        Assert.Null(UpdateFeed.FindChecksum(sums, "missing.exe"));
    }

    [Fact]
    public void VerifyChecksum_PassesOnlyOnExactMatch()
    {
        string hash = HashOf("installer-bytes");
        string sums = $"{hash}  Cetus-Setup-0.2.0.exe\n";

        Assert.True(UpdateFeed.VerifyChecksum(sums, "Cetus-Setup-0.2.0.exe", hash, out string? passed));
        Assert.Null(passed);

        Assert.False(UpdateFeed.VerifyChecksum(sums, "Cetus-Setup-0.2.0.exe", new string('0', 64), out string? mismatch));
        Assert.NotNull(mismatch);

        Assert.False(UpdateFeed.VerifyChecksum(sums, "other.exe", hash, out string? missing));
        Assert.NotNull(missing);
    }

    private static string HashOf(string content) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
