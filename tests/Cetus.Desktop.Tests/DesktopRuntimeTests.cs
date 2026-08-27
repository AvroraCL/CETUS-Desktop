using Cetus.Browser;
using Cetus.Configuration;
using Cetus.Hosting;
using Cetus.Runtime;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DesktopRuntimeTests
{
    [Fact]
    public async Task StartAsync_OrdersHostBeforeBrowser_AndPublishesReadyState()
    {
        using var scope = new RuntimeTestScope();
        var calls = new List<string>();
        var host = new FakeDshHost(calls);
        var browser = new FakeBrowserSession(calls);
        var runtime = scope.CreateRuntime(browser, new FakeDshHostFactory(_ => host));
        var phases = new List<DesktopRuntimePhase>();
        runtime.StateChanged += (_, e) => phases.Add(e.State.Phase);

        DesktopRuntimeResult result = await runtime.StartAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(["browser:hide", "host:start", "browser:navigate"], calls);
        Assert.Equal(
            [DesktopRuntimePhase.StartingHost, DesktopRuntimePhase.LoadingBrowser, DesktopRuntimePhase.Ready],
            phases);
        Assert.Equal(DesktopRuntimePhase.Ready, runtime.State.Phase);
        Assert.Equal(new Uri("http://127.0.0.1:3080/"), browser.Navigations.Single());
    }

    [Fact]
    public async Task StartAsync_WhenHostFails_StopsOwnershipAndPublishesTheError()
    {
        using var scope = new RuntimeTestScope();
        var expected = new InvalidOperationException("runtime failed");
        var host = new FakeDshHost { StartError = expected };
        var runtime = scope.CreateRuntime(
            new FakeBrowserSession(),
            new FakeDshHostFactory(_ => host));

        DesktopRuntimeResult result = await runtime.StartAsync();

        Assert.False(result.Succeeded);
        Assert.Same(expected, result.Error);
        Assert.Equal(1, host.StopCount);
        Assert.Equal(DesktopRuntimePhase.Failed, runtime.State.Phase);
        Assert.Same(expected, runtime.State.Error);
    }

    [Fact]
    public async Task ChangePortAsync_RecreatesHostAndNavigatesToTheNewOrigin()
    {
        using var scope = new RuntimeTestScope();
        var browser = new FakeBrowserSession();
        var factory = new FakeDshHostFactory(_ => new FakeDshHost());
        var runtime = scope.CreateRuntime(browser, factory);
        Assert.True((await runtime.StartAsync()).Succeeded);

        PortChangeResult result = await runtime.ChangePortAsync(4312);

        Assert.True(result.Saved);
        Assert.False(result.IsEnvironmentOverridden);
        Assert.True(result.ReconnectResult.Succeeded);
        Assert.Equal(2, factory.Endpoints.Count);
        Assert.Equal(new Uri("http://127.0.0.1:3080/"), factory.Endpoints[0]);
        Assert.Equal(new Uri("http://127.0.0.1:4312/"), factory.Endpoints[1]);
        Assert.Equal(1, factory.Hosts[0].StopCount);
        Assert.Equal(new Uri("http://127.0.0.1:4312/"), browser.Navigations[^1]);
    }

    [Fact]
    public async Task RuntimeFailure_RestartsTheOwnedHostThroughTheSameStateMachine()
    {
        using var scope = new RuntimeTestScope();
        var host = new FakeDshHost();
        var runtime = scope.CreateRuntime(
            new FakeBrowserSession(),
            new FakeDshHostFactory(_ => host));
        Assert.True((await runtime.StartAsync()).Succeeded);

        host.RaiseFailure(new DshHostFailureEventArgs(
            DshHostFailureKind.ProcessExited,
            17,
            "dsh.log"));

        Assert.Equal(2, host.StartCount);
        Assert.Equal(1, host.StopCount);
        Assert.Equal(DesktopRuntimePhase.Ready, runtime.State.Phase);
    }

    [Fact]
    public async Task FailureDuringBrowserInitialization_IsRecoveredAfterStartupLeavesItsCriticalSection()
    {
        using var scope = new RuntimeTestScope();
        var host = new FakeDshHost();
        var browser = new FakeBrowserSession();
        bool raised = false;
        browser.BeforeNavigate = () =>
        {
            if (!raised)
            {
                raised = true;
                host.RaiseFailure(new DshHostFailureEventArgs(
                    DshHostFailureKind.HealthCheckFailed,
                    null,
                    null));
            }
        };
        var runtime = scope.CreateRuntime(browser, new FakeDshHostFactory(_ => host));

        Assert.True((await runtime.StartAsync()).Succeeded);

        Assert.Equal(2, host.StartCount);
        Assert.Equal(1, host.StopCount);
        Assert.Equal(DesktopRuntimePhase.Ready, runtime.State.Phase);
    }

    [Fact]
    public async Task StopAsync_CancelsWorkAndStopsTheOwnedHostOnce()
    {
        using var scope = new RuntimeTestScope();
        var host = new FakeDshHost();
        var browser = new FakeBrowserSession();
        var runtime = scope.CreateRuntime(browser, new FakeDshHostFactory(_ => host));
        Assert.True((await runtime.StartAsync()).Succeeded);

        await runtime.StopAsync();
        await runtime.StopAsync();

        Assert.Equal(1, host.StopCount);
        Assert.True(browser.HideCount >= 2);
        Assert.Equal(DesktopRuntimePhase.Stopped, runtime.State.Phase);
        Assert.False(runtime.State.CanRetry);
    }

    private sealed class RuntimeTestScope : IDisposable
    {
        private readonly string? _originalPort;
        private readonly string _directory;

        public RuntimeTestScope()
        {
            _originalPort = Environment.GetEnvironmentVariable("CETUS_PORT");
            Environment.SetEnvironmentVariable("CETUS_PORT", null);
            _directory = TestWorkspace.CreateDirectory();
            Settings = new CetusSettings(Path.Combine(_directory, "settings.json"));
        }

        public CetusSettings Settings { get; }

        public DesktopRuntime CreateRuntime(
            IBrowserSession browser,
            IDshHostFactory hostFactory) =>
            new(
                Settings,
                browser,
                static action => action(),
                hostFactory,
                TimeProvider.System,
                static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CETUS_PORT", _originalPort);
            if (TestWorkspace.RetainArtifacts) return;
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Leave failed-test artifacts for diagnosis.
            }
        }
    }

    private sealed class FakeBrowserSession(List<string>? calls = null) : IBrowserSession
    {
        public List<Uri> Navigations { get; } = [];

        public int HideCount { get; private set; }

        public Action? BeforeNavigate { get; set; }

        public Task NavigateAsync(Uri trustedOrigin, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeNavigate?.Invoke();
            calls?.Add("browser:navigate");
            Navigations.Add(trustedOrigin);
            return Task.CompletedTask;
        }

        public void Hide()
        {
            HideCount++;
            calls?.Add("browser:hide");
        }
    }

    private sealed class FakeDshHostFactory(Func<Uri, FakeDshHost> create) : IDshHostFactory
    {
        public List<Uri> Endpoints { get; } = [];

        public List<FakeDshHost> Hosts { get; } = [];

        public IDshHost Create(Uri endpoint, string? dshHomeOverride)
        {
            Endpoints.Add(endpoint);
            FakeDshHost host = create(endpoint);
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class FakeDshHost(List<string>? calls = null) : IDshHost
    {
        public event EventHandler<DshHostFailureEventArgs>? RuntimeFailure;

        public string? LogPath => null;

        public Exception? StartError { get; set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            calls?.Add("host:start");
            return StartError is null ? Task.CompletedTask : Task.FromException(StartError);
        }

        public Task StopAsync()
        {
            StopCount++;
            calls?.Add("host:stop");
            return Task.CompletedTask;
        }

        public void RaiseFailure(DshHostFailureEventArgs failure) =>
            RuntimeFailure?.Invoke(this, failure);

        public void Dispose()
        {
        }
    }
}
