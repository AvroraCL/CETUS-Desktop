using System.Net;
using System.Net.Sockets;
using System.Text;
using Cetus.Configuration;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// Failure-classification coverage for the parts of the host that decide
/// whether to kill DSH. A wrong answer here costs a live agent turn, so these
/// cases are pinned: auth rejection must never kill, an endpoint owned by a
/// process that no longer exists must never be adopted, and a real outage must
/// still be reported.
/// </summary>
public sealed class DshHostFailureClassificationTests
{
    [Fact]
    public async Task StartAsync_EndpointRejectingTheSessionCookie_IsTreatedAsOccupied()
    {
        using var environment = new EnvironmentScope();
        int port = FreePort();
        using var server = new TestHttpServer(port, HttpStatusCode.Unauthorized, "unauthorized");
        var host = new DshHost(
            new DshCommand("missing-node.exe", "missing-entry.js", UseShim: false),
            $"http://127.0.0.1:{port}/",
            dshHomeOverride: null,
            new DshHostOptions(PortOccupiedGraceSeconds: 0, MonitorInterval: TimeSpan.FromMilliseconds(50)));

        await Assert.ThrowsAsync<DshPortOccupiedException>(() => host.StartAsync());
    }

    [Fact]
    public async Task StartAsync_HealthyEndpointOwnedByDeadProcess_IsNotAdopted()
    {
        using var environment = new EnvironmentScope();
        int port = FreePort();

        // A healthy answer plus an ownership record that points at a process
        // which no longer exists is exactly the leftover sidecar from a
        // previous Cetus run: it is alive now and dies with the old job object.
        HostOwnerState.Write(DeadProcessId(), DateTimeOffset.UtcNow.AddHours(-1));

        using var server = new TestHttpServer(port, HttpStatusCode.OK, "<html><div id=\"root\"></div></html>");
        var host = new DshHost(
            new DshCommand("missing-node.exe", "missing-entry.js", UseShim: false),
            $"http://127.0.0.1:{port}/",
            dshHomeOverride: null,
            new DshHostOptions(PortOccupiedGraceSeconds: 0));

        await Assert.ThrowsAsync<DshPortOccupiedException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Monitor_AuthRejectionAlone_NeverKillsTheHost()
    {
        using var environment = new EnvironmentScope();
        environment.RecordLiveOwner();
        int port = FreePort();
        using var server = new TestHttpServer(port, HttpStatusCode.OK, "<html><div id=\"root\"></div></html>");
        var host = new DshHost(
            new DshCommand("missing-node.exe", "missing-entry.js", UseShim: false),
            $"http://127.0.0.1:{port}/",
            dshHomeOverride: null,
            new DshHostOptions(
                PortOccupiedGraceSeconds: 0,
                MonitorInterval: TimeSpan.FromMilliseconds(30),
                HealthFailureThreshold: 2));
        DshHostFailureEventArgs? reported = null;
        host.RuntimeFailure += (_, args) => Volatile.Write(ref reported, args);

        try
        {
            await host.StartAsync();

            // The cookie starts being refused; the host is still answering.
            server.SetResponse(HttpStatusCode.Unauthorized, "unauthorized", healthy: false);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // ~16 probes, every one rejected: no failure event, host untouched.
            Assert.Null(Volatile.Read(ref reported));
            Assert.False(await host.IsHealthyAsync());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Monitor_RealOutage_IsReportedOnceWithDetail()
    {
        using var environment = new EnvironmentScope();
        environment.RecordLiveOwner();
        int port = FreePort();
        using var server = new TestHttpServer(port, HttpStatusCode.OK, "<html><div id=\"root\"></div></html>");
        var host = new DshHost(
            new DshCommand("missing-node.exe", "missing-entry.js", UseShim: false),
            $"http://127.0.0.1:{port}/",
            dshHomeOverride: null,
            new DshHostOptions(
                PortOccupiedGraceSeconds: 0,
                MonitorInterval: TimeSpan.FromMilliseconds(30),
                HealthFailureThreshold: 2,
                ProbeTimeout: TimeSpan.FromSeconds(2)));
        var failure = new TaskCompletionSource<DshHostFailureEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int eventCount = 0;
        host.RuntimeFailure += (_, args) =>
        {
            Interlocked.Increment(ref eventCount);
            failure.TrySetResult(args);
        };

        try
        {
            await host.StartAsync();
            server.SetResponse(HttpStatusCode.ServiceUnavailable, "down", healthy: false);

            DshHostFailureEventArgs result = await failure.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(200);

            Assert.Equal(DshHostFailureKind.HealthCheckFailed, result.Kind);
            Assert.NotNull(result.Detail);
            Assert.Equal(1, Volatile.Read(ref eventCount));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int DeadProcessId()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        int pid = process.Id;
        process.WaitForExit();
        return pid;
    }

    /// <summary>Points CETUS user-data at a scratch directory for one test.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _directory = TestWorkspace.CreateDirectory();
        private readonly string? _originalUserData = Environment.GetEnvironmentVariable("CETUS_USER_DATA_DIR");
        private readonly string? _originalLogDir = Environment.GetEnvironmentVariable("CETUS_LOG_DIR");

        public EnvironmentScope()
        {
            // Keeps both the ownership marker and the runtime log off the real
            // user-data directory: every StartAsync appends to that log, and a
            // live Cetus instance plus its diagnostics depend on it.
            Environment.SetEnvironmentVariable("CETUS_USER_DATA_DIR", _directory);
            Environment.SetEnvironmentVariable("CETUS_LOG_DIR", Path.Combine(_directory, "logs"));
            HostOwnerState.Clear();
        }

        public void Dispose()
        {
            HostOwnerState.Clear();
            Environment.SetEnvironmentVariable("CETUS_USER_DATA_DIR", _originalUserData);
            Environment.SetEnvironmentVariable("CETUS_LOG_DIR", _originalLogDir);
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>
        /// Records this test process as the live owner of the endpoint, which is
        /// what lets StartAsync adopt an already-listening service instead of
        /// treating its port as foreign.
        /// </summary>
        public void RecordLiveOwner()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            HostOwnerState.Write(process.Id, process.StartTime);
        }
    }

    private sealed class FakeSidecar : IDshSidecarProcess
    {
        public event EventHandler<DshSidecarExitedEventArgs>? Exited;

        public string LogPath => "fake-sidecar.log";

        public int ProcessId => Environment.ProcessId;

        public DateTimeOffset ProcessStartedAt => DateTimeOffset.UtcNow;

        public int StopCount { get; private set; }

        public bool TryGetExitCode(out int? exitCode)
        {
            exitCode = null;
            return false;
        }

        public Task StopAsync()
        {
            StopCount++;
            Exited?.Invoke(this, new DshSidecarExitedEventArgs(0));
            return Task.CompletedTask;
        }
    }

    private sealed class TestHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serveTask;
        private volatile Response _response;

        public TestHttpServer(int port, HttpStatusCode status, string body, bool healthy = true)
        {
            _response = new Response(status, body, healthy);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serveTask = Task.Run(ServeAsync);
        }

        public void SetResponse(HttpStatusCode status, string body, bool healthy) =>
            _response = new Response(status, body, healthy);

        private async Task ServeAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    Response response = _response;
                    byte[] body = Encoding.UTF8.GetBytes(response.Body);
                    context.Response.StatusCode = (int)response.Status;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (HttpListenerException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Close();
            try
            {
                _serveTask.GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
            {
            }
            _cancellation.Dispose();
        }

        private sealed record Response(HttpStatusCode Status, string Body, bool Healthy);
    }
}
