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
/// 4. <see cref="StopAsync"/> kills the process tree without blocking the caller's thread.
/// </summary>
public sealed class DshHost
{
    private const int PortOccupiedGraceSeconds = 15;
    private const int ReadyWaitSeconds = 60;
    private const int PollIntervalMs = 500;

    private readonly DshCommand _command;
    private readonly string _url;
    private readonly string? _dshHomeOverride;
    private readonly HttpClient _client;
    private readonly object _lifecycleGate = new();
    private Process? _process;
    private FileStream? _logStream;
    private string? _logPath;
    private bool _isReady;
    private bool _isStopping;

    /// <summary>Raised when a healthy DSH sidecar started by this instance exits unexpectedly.</summary>
    public event EventHandler<DshHostExitedEventArgs>? UnexpectedExit;

    public DshHost(DshCommand command, string url, string? dshHomeOverride = null)
    {
        _command = command;
        _url = url;
        _dshHomeOverride = dshHomeOverride;
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

        lock (_lifecycleGate)
        {
            _isStopping = false;
            _isReady = false;
        }
        Process startedProcess = StartHostProcess();
        lock (_lifecycleGate)
        {
            _process = startedProcess;
        }

        try
        {
            DateTime readyDeadline = DateTime.UtcNow.AddSeconds(ReadyWaitSeconds);
            while (DateTime.UtcNow < readyDeadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(ct))
                {
                    lock (_lifecycleGate)
                    {
                        _isReady = true;
                    }
                    return;
                }

                Process? currentProcess;
                lock (_lifecycleGate)
                {
                    currentProcess = _process;
                }
                if (currentProcess is null || currentProcess.HasExited)
                {
                    string exitCode = currentProcess is null ? "未知" : currentProcess.ExitCode.ToString();
                    throw new InvalidOperationException(
                        $"DSH 主机提前退出（exit code {exitCode}）。" +
                        (_logPath is not null ? $"日志：{_logPath}" : ""));
                }
                await Task.Delay(PollIntervalMs, ct);
            }

            throw new InvalidOperationException(
                "DSH 主机在 60 秒内未能就绪。" +
                (_logPath is not null ? $"日志：{_logPath}" : ""));
        }
        catch
        {
            // We own this process because this StartAsync call created it. Do not
            // leave an unready or cancelled launch attempt running in the background.
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops the sidecar started by this instance without blocking the calling
    /// thread. A healthy DSH that was only reused has no owned process to stop.
    /// </summary>
    public async Task StopAsync()
    {
        Process? process;
        FileStream? logStream;
        lock (_lifecycleGate)
        {
            _isStopping = true;
            _isReady = false;
            process = _process;
            _process = null;
            logStream = _logStream;
            _logStream = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // Best-effort teardown; nothing sensible left to do.
            }
            finally
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
        }

        logStream?.Dispose();   // pumps end on EOF or ObjectDisposedException
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        if (!string.IsNullOrWhiteSpace(_dshHomeOverride))
        {
            psi.Environment["DSH_HOME"] = _dshHomeOverride;
        }

        if (_command.UseShim)
        {
            // dsh.cmd through cmd.exe: hidden console, with the same web options
            // as the direct Node path.
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

        // The shell owns the UI, so never launch a separate browser. The server
        // must also bind the exact port that the WebView2 health check uses.
        psi.ArgumentList.Add("--no-open");
        int port = new Uri(_url).Port;
        if (port != 3080)
        {
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());
        }
        RedirectToLog(psi);

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

        process.Exited += OnProcessExited;
        process.EnableRaisingEvents = true;

        if (_logStream is not null)
        {
            // Drain the redirected pipes into the log file so node can never
            // block on a full buffer.
            _ = PumpAsync(process.StandardOutput.BaseStream, _logStream);
            _ = PumpAsync(process.StandardError.BaseStream, _logStream);
        }
        return process;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            return;
        }

        FileStream? logStream;
        string? logPath;
        lock (_lifecycleGate)
        {
            if (_isStopping || !_isReady || !ReferenceEquals(process, _process))
            {
                return;
            }

            _isReady = false;
            _process = null;
            logStream = _logStream;
            _logStream = null;
            logPath = _logPath;
        }

        process.Exited -= OnProcessExited;
        process.Dispose();
        logStream?.Dispose();

        UnexpectedExit?.Invoke(this, new DshHostExitedEventArgs(exitCode, logPath));
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

/// <summary>Details of an unexpected exit from a Cetus-owned, healthy DSH sidecar.</summary>
public sealed class DshHostExitedEventArgs(int exitCode, string? logPath) : EventArgs
{
    public int ExitCode { get; } = exitCode;
    public string? LogPath { get; } = logPath;
}
