using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;

namespace Cetus.Hosting;

/// <summary>
/// DSH host process lifecycle (the whale-launcher pattern, ported to C#):
/// 1. health-check the URL — healthy means HTTP 200 and the body contains <c>id="root"</c>;
/// 2. if the port is occupied but not healthy, wait 15 s then fail loudly;
/// 3. otherwise spawn the host hidden and wait up to 60 s for readiness;
/// 4. <see cref="Stop"/> kills the process tree.
/// </summary>
public sealed class DshHost
{
    private const int PortOccupiedGraceSeconds = 15;
    private const int ReadyWaitSeconds = 60;
    private const int PollIntervalMs = 500;

    private readonly DshCommand _command;
    private readonly string _url;
    private readonly HttpClient _client;
    private Process? _process;

    public DshHost(DshCommand command, string url)
    {
        _command = command;
        _url = url;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (await IsHealthyAsync(ct))
        {
            return; // A healthy DSH is already running; reuse it.
        }

        int port = new Uri(_url).Port;
        if (IsPortInUse(port))
        {
            // Occupied by something else: give it a short grace window, then fail loudly.
            DateTime deadline = DateTime.UtcNow.AddSeconds(PortOccupiedGraceSeconds);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(ct))
                {
                    return;
                }
                await Task.Delay(PollIntervalMs, ct);
            }
            throw new InvalidOperationException(
                $"端口 {port} 已被其他程序占用，且不是健康的 DSH 服务。");
        }

        _process = StartHostProcess();

        DateTime readyDeadline = DateTime.UtcNow.AddSeconds(ReadyWaitSeconds);
        while (DateTime.UtcNow < readyDeadline && !_process.HasExited)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsHealthyAsync(ct))
            {
                return;
            }
            await Task.Delay(PollIntervalMs, ct);
        }

        throw new InvalidOperationException("DSH 主机在 60 秒内未能就绪。");
    }

    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
            catch
            {
                // Best-effort teardown; nothing sensible left to do.
            }
        }
        _process?.Dispose();
        _process = null;
    }

    /// <summary>
    /// The whale-launcher probe: HTTP 200 plus the shell's <c>id="root"</c> marker in the body.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(_url, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }
            string body = await response.Content.ReadAsStringAsync(ct);
            return body.Contains("id=\"root\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    private Process StartHostProcess()
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (_command.UseShim)
        {
            // dsh.cmd through cmd.exe: hidden console, args pass through.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("dsh");
            psi.ArgumentList.Add("web");
        }
        else
        {
            psi.FileName = _command.NodeExe!;
            psi.ArgumentList.Add(_command.EntryScript!);
            psi.ArgumentList.Add("web");
        }

        return Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 DSH 主机进程。");
    }
}
