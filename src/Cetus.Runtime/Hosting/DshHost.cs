namespace Cetus.Hosting;

/// <summary>
/// Deep DSH ownership module. It coordinates endpoint reuse, sidecar startup,
/// readiness, health monitoring and ordered cleanup while network and process
/// details remain internal implementations.
/// </summary>
public sealed class DshHost : IDshHost
{
    private const int PortOccupiedGraceSeconds = 15;
    private const int ReadyWaitSeconds = 60;
    private const int PollIntervalMs = 500;
    private const int HealthFailureThreshold = 3;
    private static readonly TimeSpan HealthMonitorInterval = TimeSpan.FromSeconds(2);

    private readonly DshCommand _command;
    private readonly Uri _endpoint;
    private readonly string? _dshHomeOverride;
    private readonly DshEndpointProbe _probe;
    private readonly object _lifecycleGate = new();

    private DshSidecarProcess? _sidecar;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private string? _logPath;
    private bool _isReady;
    private bool _isStopping;
    private bool _failureReported;
    private bool _disposed;

    public DshHost(DshCommand command, string url, string? dshHomeOverride = null)
    {
        _command = command;
        _endpoint = new Uri(url, UriKind.Absolute);
        _dshHomeOverride = dshHomeOverride;
        _probe = new DshEndpointProbe(_endpoint);
    }

    /// <summary>Raised after a ready DSH process exits or monitored endpoint becomes unavailable.</summary>
    public event EventHandler<DshHostFailureEventArgs>? RuntimeFailure;

    /// <summary>Sidecar log file, when this host spawned the process.</summary>
    public string? LogPath => _logPath;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (await IsHealthyAsync(cancellationToken))
        {
            MarkReadyAndStartMonitoring();
            return;
        }

        if (_probe.IsPortInUse())
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(PortOccupiedGraceSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(cancellationToken))
                {
                    MarkReadyAndStartMonitoring();
                    return;
                }
                await Task.Delay(PollIntervalMs, cancellationToken);
            }

            throw new InvalidOperationException(
                $"端口 {_endpoint.Port} 已被其他程序占用，且不是健康的 DSH 服务。");
        }

        lock (_lifecycleGate)
        {
            _isStopping = false;
            _isReady = false;
            _failureReported = false;
        }

        DshSidecarProcess sidecar = DshSidecarProcess.Start(
            _command,
            _endpoint,
            _dshHomeOverride,
            OnSidecarExited);
        _logPath = sidecar.LogPath;
        lock (_lifecycleGate)
        {
            _sidecar = sidecar;
        }

        try
        {
            DateTimeOffset readyDeadline = DateTimeOffset.UtcNow.AddSeconds(ReadyWaitSeconds);
            while (DateTimeOffset.UtcNow < readyDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(cancellationToken))
                {
                    MarkReadyAndStartMonitoring();
                    return;
                }

                DshSidecarProcess? current;
                lock (_lifecycleGate)
                {
                    current = _sidecar;
                }
                int? exitCode = null;
                if (current is null || current.TryGetExitCode(out exitCode))
                {
                    throw new InvalidOperationException(
                        $"DSH 主机提前退出（exit code {exitCode?.ToString() ?? "未知"}）。" +
                        (_logPath is not null ? $"日志：{_logPath}" : string.Empty));
                }

                await Task.Delay(PollIntervalMs, cancellationToken);
            }

            throw new InvalidOperationException(
                "DSH 主机在 60 秒内未能就绪。" +
                (_logPath is not null ? $"日志：{_logPath}" : string.Empty));
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops only the sidecar owned by this host. A reused external DSH endpoint
    /// has no sidecar and remains untouched.
    /// </summary>
    public async Task StopAsync()
    {
        DshSidecarProcess? sidecar;
        CancellationTokenSource? monitorCancellation;
        Task? monitorTask;
        lock (_lifecycleGate)
        {
            _isStopping = true;
            _isReady = false;
            sidecar = _sidecar;
            _sidecar = null;
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

        if (sidecar is not null)
        {
            sidecar.Exited -= OnSidecarExited;
            await sidecar.StopAsync();
        }
    }

    /// <summary>HTTP 200 plus the Harness shell's root marker.</summary>
    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _probe.IsHealthyAsync(cancellationToken);
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

    private async Task MonitorHealthAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        try
        {
            while (true)
            {
                await Task.Delay(HealthMonitorInterval, cancellationToken);
                if (await IsHealthyAsync(cancellationToken))
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop or retry cancelled the active monitor.
        }
    }

    private void OnSidecarExited(object? sender, DshSidecarExitedEventArgs e)
    {
        lock (_lifecycleGate)
        {
            if (_isStopping
                || !_isReady
                || _failureReported
                || !ReferenceEquals(sender, _sidecar))
            {
                return;
            }

            _isReady = false;
            _failureReported = true;
            _sidecar = null;
        }

        RuntimeFailure?.Invoke(this, new DshHostFailureEventArgs(
            DshHostFailureKind.ProcessExited,
            e.ExitCode,
            _logPath));
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (_sidecar is not null || _monitorTask is not null)
            {
                throw new InvalidOperationException("Dispose 前必须先调用 StopAsync。");
            }
            _disposed = true;
        }
        _probe.Dispose();
    }
}

public enum DshHostFailureKind
{
    ProcessExited,
    HealthCheckFailed,
}

public sealed class DshHostFailureEventArgs(
    DshHostFailureKind kind,
    int? exitCode,
    string? logPath) : EventArgs
{
    public DshHostFailureKind Kind { get; } = kind;

    public int? ExitCode { get; } = exitCode;

    public string? LogPath { get; } = logPath;
}
