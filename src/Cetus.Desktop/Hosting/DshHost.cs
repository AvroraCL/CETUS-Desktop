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
    private static readonly TimeSpan HealthMonitorInterval = TimeSpan.FromSeconds(2);
    private const int HealthFailureThreshold = 3;

    private readonly DshCommand _command;
    private readonly string _url;
    private readonly string? _dshHomeOverride;
    private readonly HttpClient _client;
    private readonly object _lifecycleGate = new();
    private Process? _process;
    private SidecarJob? _sidecarJob;
    private FileStream? _logStream;
    private string? _logPath;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private bool _isReady;
    private bool _isStopping;
    private bool _failureReported;

    /// <summary>Raised when a healthy DSH sidecar exits or any monitored DSH service becomes unavailable.</summary>
    public event EventHandler<DshHostFailureEventArgs>? RuntimeFailure;

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
            MarkReadyAndStartMonitoring();
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
                    MarkReadyAndStartMonitoring();
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
            _failureReported = false;
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
                    MarkReadyAndStartMonitoring();
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
        SidecarJob? sidecarJob;
        FileStream? logStream;
        CancellationTokenSource? monitorCancellation;
        Task? monitorTask;
        lock (_lifecycleGate)
        {
            _isStopping = true;
            _isReady = false;
            process = _process;
            _process = null;
            sidecarJob = _sidecarJob;
            _sidecarJob = null;
            logStream = _logStream;
            _logStream = null;
            monitorCancellation = _monitorCancellation;
            _monitorCancellation = null;
            monitorTask = _monitorTask;
            _monitorTask = null;
        }

        monitorCancellation?.Cancel();
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Normal monitor shutdown.
            }
        }
        monitorCancellation?.Dispose();

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
                // Closing the job below remains the authoritative tree cleanup.
            }
            finally
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
        }

        sidecarJob?.Dispose();
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

    private void MarkReadyAndStartMonitoring()
    {
        var cancellation = new CancellationTokenSource();
        lock (_lifecycleGate)
        {
            _isStopping = false;
            _isReady = true;
            _failureReported = false;
            _monitorCancellation?.Cancel();
            _monitorCancellation = cancellation;
            _monitorTask = MonitorHealthAsync(cancellation.Token);
        }
    }

    private async Task MonitorHealthAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        try
        {
            while (true)
            {
                await Task.Delay(HealthMonitorInterval, ct);
                bool healthy = await IsHealthyAsync(ct);
                if (healthy)
                {
                    consecutiveFailures = 0;
                    continue;
                }

                consecutiveFailures++;
                if (consecutiveFailures < HealthFailureThreshold)
                {
                    continue;
                }

                ReportRuntimeFailure(new DshHostFailureEventArgs(
                    DshHostFailureKind.HealthCheckFailed,
                    null,
                    _logPath));
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop/retry cancels the active monitor.
        }
    }

    private void ReportRuntimeFailure(DshHostFailureEventArgs failure)
    {
        lock (_lifecycleGate)
        {
            if (_isStopping || !_isReady || _failureReported)
            {
                return;
            }

            _isReady = false;
            _failureReported = true;
        }

        RuntimeFailure?.Invoke(this, failure);
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

        Process? process = null;
        SidecarJob? sidecarJob = null;
        try
        {
            sidecarJob = SidecarJob.Create();
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 DSH 主机进程。");
            sidecarJob.Assign(process);
            lock (_lifecycleGate)
            {
                _sidecarJob = sidecarJob;
            }
            sidecarJob = null;
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch
                {
                    // Closing a successfully created job is the remaining cleanup path.
                }
                process.Dispose();
            }
            sidecarJob?.Dispose();
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
        SidecarJob? sidecarJob;
        string? logPath;
        lock (_lifecycleGate)
        {
            if (_isStopping || !_isReady || _failureReported || !ReferenceEquals(process, _process))
            {
                return;
            }

            _isReady = false;
            _failureReported = true;
            _process = null;
            sidecarJob = _sidecarJob;
            _sidecarJob = null;
            logStream = _logStream;
            _logStream = null;
            logPath = _logPath;
        }

        process.Exited -= OnProcessExited;
        process.Dispose();
        sidecarJob?.Dispose();
        logStream?.Dispose();

        RuntimeFailure?.Invoke(this, new DshHostFailureEventArgs(
            DshHostFailureKind.ProcessExited,
            exitCode,
            logPath));
    }

    /// <summary>Sidecar stdout/stderr → %LOCALAPPDATA%\Cetus\logs\dsh-*.log.</summary>
    private void RedirectToLog(ProcessStartInfo psi)
    {
        string logDirectory = Environment.GetEnvironmentVariable("CETUS_LOG_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cetus", "logs");
        _logPath = Path.Combine(logDirectory, $"dsh-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(logDirectory);
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

/// <summary>The monitored DSH runtime failure category.</summary>
public enum DshHostFailureKind
{
    ProcessExited,
    HealthCheckFailed,
}

/// <summary>Details of a monitored DSH runtime failure.</summary>
public sealed class DshHostFailureEventArgs(
    DshHostFailureKind kind,
    int? exitCode,
    string? logPath) : EventArgs
{
    public DshHostFailureKind Kind { get; } = kind;
    public int? ExitCode { get; } = exitCode;
    public string? LogPath { get; } = logPath;
}
