using System.Diagnostics;
using System.IO;
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
    private FileStream? _logStream;
    private string? _logPath;

    public DshHost(DshCommand command, string url)
    {
        _command = command;
        _url = url;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>Sidecar log file (<c>%LOCALAPPDATA%\Cetus\logs\dsh-*.log</c>), when we spawned it.</summary>
    public string? LogPath => _logPath;

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
        while (DateTime.UtcNow < readyDeadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsHealthyAsync(ct))
            {
                return;
            }
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"DSH 主机提前退出（exit code {_process.ExitCode}）。" +
                    (_logPath is not null ? $"日志：{_logPath}" : ""));
            }
            await Task.Delay(PollIntervalMs, ct);
        }

        throw new InvalidOperationException(
            "DSH 主机在 60 秒内未能就绪。" +
            (_logPath is not null ? $"日志：{_logPath}" : ""));
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
        _logStream?.Dispose();   // pumps end on EOF or ObjectDisposedException
        _logStream = null;
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
            psi.ArgumentList.Add("--no-open");   // the shell IS the UI; don't open the default browser
            int port = new Uri(_url).Port;
            if (port != 3080)
            {
                // dsh web binds the composed default port; pass --port when overridden.
                psi.ArgumentList.Add("--port");
                psi.ArgumentList.Add(port.ToString());
            }
            RedirectToLog(psi);
        }

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 DSH 主机进程。");
        }
        catch
        {
            _logStream?.Dispose();
            _logStream = null;
            _logPath = null;
            throw;
        }

        if (_logStream is not null)
        {
            // Drain the redirected pipes into the log file so node can never
            // block on a full buffer.
            _ = PumpAsync(process.StandardOutput.BaseStream, _logStream);
            _ = PumpAsync(process.StandardError.BaseStream, _logStream);
        }
        return process;
    }

    /// <summary>Sidecar stdout/stderr → %LOCALAPPDATA%\Cetus\logs\dsh-*.log.</summary>
    private void RedirectToLog(ProcessStartInfo psi)
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cetus", "logs", $"dsh-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        _logStream = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
    }

    private static async Task PumpAsync(Stream source, Stream sink)
    {
        try
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read));
                await sink.FlushAsync();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }
}
