using System.Net;
using System.Net.Http;
using System.Text;
using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class UpdateSourceProbeTests
{
    private const string GithubUrl = "https://test/gh-feed";
    private const string GitcodeUrl = "https://api.gitcode.test/tags";

    [Fact]
    public async Task ProbeFasterSource_PicksTheFasterReachableSource()
    {
        // GitCode answers quickly; GitHub stalls beyond the probe window.
        using var service = new UpdateService(
            new LatencyHandler(
                githubDelay: TimeSpan.FromMilliseconds(600),
                gitcodeDelay: TimeSpan.FromMilliseconds(10)),
            githubFeed: GithubUrl);

        UpdateFeedSource? faster = await service.ProbeFasterSourceAsync(
            UpdateFeedSource.GitHub, CancellationToken.None);

        Assert.Equal(UpdateFeedSource.GitCode, faster);
    }

    [Fact]
    public async Task ProbeFasterSource_PreferredWinsWithinItsWindow()
    {
        // GitHub answers a touch faster than GitCode.
        using var service = new UpdateService(
            new LatencyHandler(
                githubDelay: TimeSpan.FromMilliseconds(10),
                gitcodeDelay: TimeSpan.FromMilliseconds(120)),
            githubFeed: GithubUrl);

        UpdateFeedSource? faster = await service.ProbeFasterSourceAsync(
            UpdateFeedSource.GitHub, CancellationToken.None);

        Assert.Equal(UpdateFeedSource.GitHub, faster);
    }

    [Fact]
    public async Task ProbeFasterSource_UnreachablePreferred_FallsToTheOtherSource()
    {
        using var service = new UpdateService(
            new LatencyHandler(
                githubDelay: TimeSpan.FromMilliseconds(10),
                githubStatus: HttpStatusCode.ServiceUnavailable,
                gitcodeDelay: TimeSpan.FromMilliseconds(30)),
            githubFeed: GithubUrl);

        UpdateFeedSource? faster = await service.ProbeFasterSourceAsync(
            UpdateFeedSource.GitHub, CancellationToken.None);

        Assert.Equal(UpdateFeedSource.GitCode, faster);
    }

    [Fact]
    public async Task ProbeFasterSource_BothUnreachable_YieldsNull()
    {
        using var service = new UpdateService(
            new LatencyHandler(
                githubDelay: TimeSpan.FromMilliseconds(10),
                githubStatus: HttpStatusCode.ServiceUnavailable,
                gitcodeDelay: TimeSpan.FromMilliseconds(10),
                gitcodeStatus: HttpStatusCode.ServiceUnavailable),
            githubFeed: GithubUrl);

        Assert.Null(await service.ProbeFasterSourceAsync(UpdateFeedSource.GitHub, CancellationToken.None));
    }

    private sealed class LatencyHandler(
        TimeSpan githubDelay,
        TimeSpan gitcodeDelay,
        HttpStatusCode githubStatus = HttpStatusCode.OK,
        HttpStatusCode gitcodeStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            bool isGithub = request.RequestUri!.ToString().StartsWith(GithubUrl, StringComparison.Ordinal);
            await Task.Delay(isGithub ? githubDelay : gitcodeDelay, cancellationToken);
            return new HttpResponseMessage(isGithub ? githubStatus : gitcodeStatus)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
        }
    }
}
