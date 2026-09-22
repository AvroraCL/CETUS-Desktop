using System.Net;
using System.Net.Http;
using System.Text;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshRuntimeInfoTests
{
    [Fact]
    public void ReadVersionsFile_ParsesTheDshLine()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "VERSIONS.txt");
        File.WriteAllLines(path,
        [
            "cetus=0.3.0",
            "node=v24.14.0",
            "dsh=0.1.7-alpha.1",
            "built=2026-09-20",
        ]);

        Assert.Equal("0.1.7-alpha.1", DshRuntimeInfo.ReadVersionsFile(path));
    }

    [Fact]
    public void ReadVersionsFile_MissingOrWithoutDshLine_YieldsNull()
    {
        using var directory = new TemporaryDirectory();
        string missing = System.IO.Path.Combine(directory.Path, "missing.txt");
        Assert.Null(DshRuntimeInfo.ReadVersionsFile(missing));

        string path = System.IO.Path.Combine(directory.Path, "VERSIONS.txt");
        File.WriteAllText(path, "cetus=0.3.0\nnode=v24.14.0\n");
        Assert.Null(DshRuntimeInfo.ReadVersionsFile(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestWorkspace.CreateDirectory();
        }

        public string Path { get; }

        public void Dispose()
        {
            if (TestWorkspace.RetainArtifacts) return;
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Leave failed-test artifacts for diagnosis.
            }
        }
    }
}

public sealed class NpmDistTagFeedTests
{
    [Fact]
    public async Task FetchAsync_ParsesLatestAndAlphaTags()
    {
        var handler = new FakeHandler(
            HttpStatusCode.OK,
            """{ "latest": "0.1.6-alpha.2", "alpha": "0.1.7-alpha.1", "next": "0.1.7-alpha.1" }""");
        using var feed = new NpmDistTagFeed("https://registry.test/dsh/dist-tags", handler);

        DshDistTags? tags = await feed.FetchAsync(CancellationToken.None);

        Assert.NotNull(tags);
        Assert.Equal("0.1.6-alpha.2", tags.Latest);
        Assert.Equal("0.1.7-alpha.1", tags.Alpha);
    }

    [Fact]
    public void HighestAvailable_PrefersNewerAlphaOverAnOlderLatestTag()
    {
        var tags = new DshDistTags("0.1.5-rc.3", "0.1.7-alpha.1");

        DshDistTag highest = Assert.IsType<DshDistTag>(tags.HighestAvailable);

        Assert.Equal("alpha", highest.Channel);
        Assert.Equal("0.1.7-alpha.1", highest.Version);
    }

    [Theory]
    [InlineData("0.1.7-alpha.1", "0.1.6-alpha.2", 1)]
    [InlineData("0.1.7-alpha.1", "0.1.7-alpha.2", -1)]
    [InlineData("0.1.7", "0.1.7-rc.3", 1)]
    [InlineData("0.1.7-beta.11", "0.1.7-beta.2", 1)]
    public void DshVersion_OrdersPrereleaseVersions(string left, string right, int expectedSign)
    {
        Assert.True(DshVersion.TryParse(left, out DshVersion? parsedLeft));
        Assert.True(DshVersion.TryParse(right, out DshVersion? parsedRight));
        Assert.NotNull(parsedLeft);
        Assert.NotNull(parsedRight);

        Assert.Equal(expectedSign, Math.Sign(DshVersion.Compare(parsedLeft, parsedRight)));
    }

    [Fact]
    public async Task FetchAsync_ServerError_YieldsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "boom");
        using var feed = new NpmDistTagFeed("https://registry.test/dsh/dist-tags", handler);

        Assert.Null(await feed.FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_MalformedJson_YieldsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{ not json");
        using var feed = new NpmDistTagFeed("https://registry.test/dsh/dist-tags", handler);

        Assert.Null(await feed.FetchAsync(CancellationToken.None));
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
