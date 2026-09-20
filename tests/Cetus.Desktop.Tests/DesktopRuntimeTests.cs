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

    [Fact]
    public async Task StartAsync_PortOccupied_SavesFreePortAndRecovers()
    {
        using var scope = new RuntimeTestScope();
        var occupied = new FakeDshHost { StartError = new DshPortOccupiedException(3080) };
        var healthy = new FakeDshHost();
        int created = 0;
        var factory = new FakeDshHostFactory(_ => ++created == 1 ? occupied : healthy);
        var browser = new FakeBrowserSession();
        var runtime = scope.CreateRuntime(browser, factory);
        var fallbacks = new List<DshPortFallbackEventArgs>();
        runtime.PortFallback += (_, e) => fallbacks.Add(e);

        DesktopRuntimeResult result = await runtime.StartAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(DesktopRuntimePhase.Ready, runtime.State.Phase);
        Assert.Equal(2, factory.Endpoints.Count);
        Assert.Equal(3080, factory.Endpoints[0].Port);
        Assert.Equal(1, occupied.StartCount);
        Assert.Equal(1, occupied.StopCount);
        Assert.Equal(1, healthy.StartCount);

        DshPortFallbackEventArgs fallback = Assert.Single(fallbacks);
        Assert.Equal(3080, fallback.PreviousPort);
        Assert.Equal(fallback.NewPort, factory.Endpoints[1].Port);
        Assert.InRange(fallback.NewPort, 1, 65535);
        Assert.NotEqual(3080, fallback.NewPort);
        Assert.Equal(
            fallback.NewPort,
            new CetusSettings(Path.Combine(scope.DirectoryPath, "settings.json")).ConfiguredPort);
        Assert.Equal(new Uri($"http://127.0.0.1:{fallback.NewPort}/"), browser.Navigations.Single());
    }

    [Fact]
    public async Task StartAsync_PortOccupiedUnderEnvironmentOverride_DoesNotSelfHeal()
    {
        using var scope = new RuntimeTestScope();
        Environment.SetEnvironmentVariable("CETUS_PORT", "4310");
        try
        {
            var host = new FakeDshHost { StartError = new DshPortOccupiedException(4310) };
            var runtime = scope.CreateRuntime(
                new FakeBrowserSession(),
                new FakeDshHostFactory(_ => host));
            bool fallbackRaised = false;
            runtime.PortFallback += (_, _) => fallbackRaised = true;

            DesktopRuntimeResult result = await runtime.StartAsync();

            Assert.False(result.Succeeded);
            Assert.Same(host.StartError, result.Error);
            Assert.Equal(1, host.StartCount);
            Assert.False(fallbackRaised);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_PORT", null);
        }
    }

    [Fact]
    public async Task StartAsync_FallbackPortAlsoOccupied_FailsAfterSingleRetry()
    {
        using var scope = new RuntimeTestScope();
        var factory = new FakeDshHostFactory(_ => new FakeDshHost
        {
            StartError = new DshPortOccupiedException(4300),
        });
        var runtime = scope.CreateRuntime(
            new FakeBrowserSession(),
            factory);
        var fallbacks = new List<DshPortFallbackEventArgs>();
        runtime.PortFallback += (_, e) => fallbacks.Add(e);

        DesktopRuntimeResult result = await runtime.StartAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(DesktopRuntimePhase.Failed, runtime.State.Phase);
        Assert.Equal(2, factory.Endpoints.Count);
        Assert.Single(fallbacks);
    }

    [Fact]
    public async Task StartAsync_FailureParkedAfterReady_DoesNotReplayOnTheNextRuntime()
    {
        using var scope = new RuntimeTestScope();
        var firstHost = new FakeDshHost();
        var browser = new FakeBrowserSession();
        var first = scope.CreateRuntime(browser, new FakeDshHostFactory(_ => firstHost));
        _ = await first.StartAsync();

        // The host reports a failure while a later navigation is in flight, so
        // it is parked until the startup critical section ends; reaching Ready
        // replays it once.
        browser.BeforeNavigate = () => firstHost.RaiseFailure(new DshHostFailureEventArgs(
            DshHostFailureKind.HealthCheckFailed,
            null,
            null,
            "probe"));
        await first.NavigateHomeAsync();
        await first.StopAsync();

        // A fresh runtime with a healthy host must not inherit the parked
        // failure: replaying it would kill a host that just came up, which is
        // the "it restarted right after opening" report.
        var healthyHost = new FakeDshHost();
        var second = scope.CreateRuntime(new FakeBrowserSession(), new FakeDshHostFactory(_ => healthyHost));

        DesktopRuntimeResult succeeded = await second.StartAsync();

        Assert.True(succeeded.Succeeded);
        Assert.Equal(DesktopRuntimePhase.Ready, second.State.Phase);
        Assert.Equal(1, healthyHost.StartCount);
        Assert.Equal(0, healthyHost.StopCount);
    }

    [Fact]
    public async Task NavigateHomeAsync_FromReady_ReturnsToReadyAndKeepsRetryEnabled()
    {
        using var scope = new RuntimeTestScope();
        var host = new FakeDshHost();
        var browser = new FakeBrowserSession();
        var runtime = scope.CreateRuntime(browser, new FakeDshHostFactory(_ => host));
        Assert.True((await runtime.StartAsync()).Succeeded);

        await runtime.NavigateHomeAsync();

        // Workspace activation / Jump List navigations must not strand the
        // runtime in LoadingBrowser, which used to disable the tray retry item.
        Assert.Equal(DesktopRuntimePhase.Ready, runtime.State.Phase);
        Assert.True(runtime.State.CanRetry);
        Assert.Equal(2, browser.Navigations.Count);
        Assert.Equal(1, host.StartCount);
        Assert.Equal(0, host.StopCount);
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

        public string DirectoryPath => _directory;

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
