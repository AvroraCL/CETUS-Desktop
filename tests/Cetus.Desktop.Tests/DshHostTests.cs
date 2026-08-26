using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cetus.Hosting;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cetus.Desktop.Tests;

public sealed class DshHostTests
{
    [Fact]
    public async Task StartAsync_DirectNode_PassesWebUiAndCustomPortArguments()
    {
        int port = GetFreeLoopbackPort();
        using var fixture = new NodeServerFixture(healthy: true);
        var host = new DshHost(
            new DshCommand(FindNodeExecutable(), fixture.EntryScript, UseShim: false),
            LocalUrl(port));

        try
        {
            await host.StartAsync();
            Assert.True(await host.IsHealthyAsync());
        }
        finally
        {
            await host.StopAsync();
        }

        await WaitForPortReleaseAsync(port);
    }

    [Fact]
    public async Task StartAsync_AppliesDshHomeOverrideToOwnedSidecar()
    {
        int port = GetFreeLoopbackPort();
        string dshHome = Path.Combine(Path.GetTempPath(), "CetusTests", Guid.NewGuid().ToString("N"));
        using var fixture = new NodeServerFixture(healthy: true, expectedDshHome: dshHome);
        var host = new DshHost(
            new DshCommand(FindNodeExecutable(), fixture.EntryScript, UseShim: false),
            LocalUrl(port),
            dshHome);

        try
        {
            await host.StartAsync();
            Assert.True(await host.IsHealthyAsync());
        }
        finally
        {
            await host.StopAsync();
        }

        await WaitForPortReleaseAsync(port);
    }

    [Fact]
    public void Resolve_UsesConfiguredNodeAndDshOverrides()
    {
        using var fixture = new NodeServerFixture(healthy: true);
        string nodePath = Path.Combine(fixture.DirectoryPath, "node.exe");
        File.WriteAllText(nodePath, string.Empty);

        string? originalNode = Environment.GetEnvironmentVariable("CETUS_NODE_EXE");
        string? originalEntry = Environment.GetEnvironmentVariable("CETUS_DSH_ENTRY");
        try
        {
            Environment.SetEnvironmentVariable("CETUS_NODE_EXE", nodePath);
            Environment.SetEnvironmentVariable("CETUS_DSH_ENTRY", fixture.EntryScript);

            DshCommand command = DshLocator.Resolve();

            Assert.False(command.UseShim);
            Assert.Equal(nodePath, command.NodeExe);
            Assert.Equal(fixture.EntryScript, command.EntryScript);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_NODE_EXE", originalNode);
            Environment.SetEnvironmentVariable("CETUS_DSH_ENTRY", originalEntry);
        }
    }

    [Fact]
    public async Task StartAsync_Shim_PassesWebUiAndCustomPortArguments()
    {
        int port = GetFreeLoopbackPort();
        using var fixture = new NodeServerFixture(healthy: true);
        fixture.CreateDshShim(FindNodeExecutable());

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH", fixture.DirectoryPath + Path.PathSeparator + originalPath);

            var host = new DshHost(
                new DshCommand(null, null, UseShim: true),
                LocalUrl(port));
            try
            {
                await host.StartAsync();
                Assert.True(await host.IsHealthyAsync());
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        await WaitForPortReleaseAsync(port);
    }

    [Fact]
    public async Task StartAsync_ReusesHealthyServiceWithoutNeedingALaunchCommand()
    {
        int port = GetFreeLoopbackPort();
        using var server = new RootHttpServer(port);
        var host = new DshHost(
            new DshCommand("missing-node.exe", "missing-entry.js", UseShim: false),
            LocalUrl(port));

        await host.StartAsync();

        Assert.True(server.RequestCount > 0);
        Assert.True(await host.IsHealthyAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task StartAsync_CancellationStopsAnUnreadyOwnedSidecar()
    {
        int port = GetFreeLoopbackPort();
        using var fixture = new NodeServerFixture(healthy: false);
        var host = new DshHost(
            new DshCommand(FindNodeExecutable(), fixture.EntryScript, UseShim: false),
            LocalUrl(port));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.StartAsync(cancellation.Token));

        await WaitForPortReleaseAsync(port);
    }

    private static string LocalUrl(int port) => $"http://127.0.0.1:{port}/";

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForPortReleaseAsync(int port)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"Port {port} remained occupied after the sidecar stopped.");
    }

    private static string FindNodeExecutable()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("CETUS_TEST_NODE_EXE");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory.Trim('"'), "node.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            "Node.js is required for DshHost lifecycle tests. Set CETUS_TEST_NODE_EXE when it is not on PATH.");
    }

    private sealed class NodeServerFixture : IDisposable
    {
        private const string HealthyBody = "<html><div id=\"root\"></div></html>";
        private const string UnhealthyBody = "<html><div id=\"not-root\"></div></html>";

        public NodeServerFixture(bool healthy, string? expectedDshHome = null)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "CetusTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            EntryScript = Path.Combine(DirectoryPath, "fake-dsh.js");
            string body = healthy ? HealthyBody : UnhealthyBody;
            string dshHomeCheck = expectedDshHome is null
                ? string.Empty
                : $"if (process.env.DSH_HOME !== {JsonSerializer.Serialize(expectedDshHome)}) {{ process.exit(43); }}";
            File.WriteAllText(EntryScript, $$"""
                const http = require('http');
                const args = process.argv.slice(2);
                const portIndex = args.indexOf('--port');
                if (!args.includes('web') || !args.includes('--no-open') || portIndex < 0) {
                  process.exit(41);
                }
                const port = Number(args[portIndex + 1]);
                if (!Number.isInteger(port) || port < 1 || port > 65535) {
                  process.exit(42);
                }
                {{dshHomeCheck}}
                http.createServer((request, response) => {
                  response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
                  response.end('{{body}}');
                }).listen(port, '127.0.0.1');
                """);
        }

        public string DirectoryPath { get; }
        public string EntryScript { get; }

        public void CreateDshShim(string nodeExecutable)
        {
            string shimPath = Path.Combine(DirectoryPath, "dsh.cmd");
            File.WriteAllText(
                shimPath,
                $"@echo off{Environment.NewLine}\"{nodeExecutable}\" \"{EntryScript}\" %*{Environment.NewLine}",
                Encoding.ASCII);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // A failed test can still have the child process exiting; leave the temporary fixture for diagnosis.
            }
        }
    }

    private sealed class RootHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serveTask;
        private int _requestCount;

        public RootHttpServer(int port)
        {
            _listener.Prefixes.Add(LocalUrl(port));
            _listener.Start();
            _serveTask = Task.Run(ServeAsync);
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _requestCount);
                    byte[] body = Encoding.UTF8.GetBytes("<html><div id=\"root\"></div></html>");
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
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
            catch (HttpListenerException)
            {
                // Closing HttpListener ends the accept loop.
            }
            catch (ObjectDisposedException)
            {
                // Closing HttpListener ends the accept loop.
            }
            _cancellation.Dispose();
        }
    }
}
