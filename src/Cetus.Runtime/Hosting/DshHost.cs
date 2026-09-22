using System.Diagnostics;
using System.IO;
using Cetus.Configuration;
using Cetus.DshStatus;

namespace Cetus.Hosting;

/// <summary>Sidecar operations the host needs. Keeps process details replaceable in tests.</summary>
internal interface IDshSidecarProcess
{
    event EventHandler<DshSidecarExitedEventArgs>? Exited;

    string LogPath { get; }

    int ProcessId { get; }

    /// <summary>When the sidecar started; lets a stale record be rejected early.</summary>
    DateTimeOffset ProcessStartedAt => DateTimeOffset.MinValue;

    bool TryGetExitCode(out int? exitCode);

    Task StopAsync();
}

/// <summary>Timing knobs. Defaults are production values; tests inject short ones.</summary>
internal sealed record DshHostOptions(
    int PortOccupiedGraceSeconds = 15,
    int ReadyWaitSeconds = 90,
    TimeSpan? MonitorInterval = null,
    int HealthFailureThreshold = 10,
    TimeSpan? ProbeTimeout = null)
{
    internal TimeSpan EffectiveMonitorInterval => MonitorInterval ?? TimeSpan.FromSeconds(3);

    internal TimeSpan EffectiveProbeTimeout => ProbeTimeout ?? TimeSpan.FromSeconds(5);
}

/// <summary>
/// Deep DSH ownership module. It coordinates endpoint reuse, sidecar startup,
/// readiness, health monitoring and ordered cleanup while network and process
/// details remain internal implementations.
/// </summary>
public sealed class DshHost : IDshHost
{
    private const int PollIntervalMs = 500;

    private readonly DshCommand _command;
    private readonly Uri _endpoint;
    private readonly string? _dshHomeOverride;
    private readonly DshHostOptions _options;
    private readonly DshEndpointProbe _probe;
    private readonly Func<DshCommand, Uri, string?, EventHandler<DshSidecarExitedEventArgs>, IDshSidecarProcess>
        _sidecarFactory;
    private readonly object _lifecycleGate = new();

    private IDshSidecarProcess? _sidecar;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private int _monitorGeneration;
    private string? _logPath;
    private bool _isReady;
    private bool _isStopping;
    private bool _failureReported;
    private bool _disposed;
    private string? _observedOwner;

    public DshHost(DshCommand command, string url, string? dshHomeOverride = null)
        : this(command, url, dshHomeOverride, new DshHostOptions())
    {
    }

    internal DshHost(DshCommand command, string url, string? dshHomeOverride, int portOccupiedGraceSeconds)
        : this(command, url, dshHomeOverride, new DshHostOptions(PortOccupiedGraceSeconds: portOccupiedGraceSeconds))
    {
    }

    internal DshHost(DshCommand command, string url, string? dshHomeOverride, DshHostOptions options)
        : this(command, url, dshHomeOverride, options, DefaultSidecarFactory)
    {
    }

    internal DshHost(
        DshCommand command,
        string url,
        string? dshHomeOverride,
        DshHostOptions options,
        Func<DshCommand, Uri, string?, EventHandler<DshSidecarExitedEventArgs>, IDshSidecarProcess> sidecarFactory)
    {
        _command = command;
        _endpoint = new Uri(url, UriKind.Absolute);
        _dshHomeOverride = dshHomeOverride;
        _options = options;
        _sidecarFactory = sidecarFactory;
        _probe = new DshEndpointProbe(_endpoint, _dshHomeOverride, options.EffectiveProbeTimeout);
    }

    /// <summary>Raised after a ready DSH process exits or monitored endpoint becomes unavailable.</summary>
    public event EventHandler<DshHostFailureEventArgs>? RuntimeFailure;

    /// <summary>Sidecar log file, when this host spawned the process.</summary>
    public string? LogPath => _logPath;

    private static IDshSidecarProcess DefaultSidecarFactory(
        DshCommand command,
        Uri endpoint,
        string? dshHomeOverride,
        EventHandler<DshSidecarExitedEventArgs> exited) =>
        DshSidecarProcess.Start(command, endpoint, dshHomeOverride, exited);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ResetLifecycleState();

        DshProbeResult probe = await _probe.ProbeAsync(cancellationToken);
        if (probe.IsHealthy)
        {
            // A healthy answer is not enough: a sidecar orphaned by a previous
            // Cetus run answers exactly like one of ours and then dies with its
            // original owner's Job Object. Only adopt what we can prove is alive.
            if (IsRecordedOwnerAlive(out string? ownerDetail))
            {
                EnsureLoopbackBinding();
                _observedOwner = $"adopted endpoint（{ownerDetail}）";
                MarkReadyAndStartMonitoring();
                return;
            }

            if (ownerDetail is not null)
            {
                RuntimeLog.Append(
                    $"stale endpoint detected: {_endpoint} answers but {ownerDetail}; treating the port as occupied");
            }
        }

        if (probe.Status == DshProbeStatus.AuthRejected || _probe.IsPortInUse())
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(_options.PortOccupiedGraceSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DshProbeResult retry = await _probe.ProbeAsync(cancellationToken);
                if (retry.IsHealthy)
                {
                    EnsureLoopbackBinding();
                    MarkReadyAndStartMonitoring();
                    return;
                }

                await Task.Delay(PollIntervalMs, cancellationToken);
            }

            throw new DshPortOccupiedException(_endpoint.Port);
        }

        DshAuth.EnsureSessionSecret(_dshHomeOverride);

        // Best-effort hygiene: the credentials file must stay plaintext for
        // DSH, so the defense is a user-only ACL. Failures never block start.
        CredentialGuard.EnsureUserOnlyAccess(
            Path.Combine(DshCredentials.ResolveDshHome(_dshHomeOverride), ".credentials.yaml"));

        RuntimeLog.Append($"DSH spawn: endpoint={_endpoint}, grace={_options.PortOccupiedGraceSeconds}s");
        IDshSidecarProcess sidecar = _sidecarFactory(_command, _endpoint, _dshHomeOverride, OnSidecarExited);
        _logPath = sidecar.LogPath;
        lock (_lifecycleGate)
        {
            _sidecar = sidecar;
        }

        HostOwnerState.Write(sidecar.ProcessId, sidecar.ProcessStartedAt);
        _observedOwner = $"spawned pid={sidecar.ProcessId}";

        try
        {
            DateTimeOffset readyDeadline = DateTimeOffset.UtcNow.AddSeconds(_options.ReadyWaitSeconds);
            while (DateTimeOffset.UtcNow < readyDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((await _probe.ProbeAsync(cancellationToken)).IsHealthy)
                {
                    await RequireLoopbackBindingAsync();
                    MarkReadyAndStartMonitoring();
                    return;
                }

                IDshSidecarProcess? current;
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
                $"DSH 主机在 {_options.ReadyWaitSeconds} 秒内未能就绪。" +
                (_logPath is not null ? $"日志：{_logPath}" : string.Empty));
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    private void ResetLifecycleState()
    {
        lock (_lifecycleGate)
        {
            _isStopping = false;
            _isReady = false;
            _failureReported = false;
        }
    }

    /// <summary>
    /// Stops only the sidecar owned by this host. A reused external DSH endpoint
    /// has no sidecar and remains untouched.
    /// </summary>
    public async Task StopAsync()
    {
        IDshSidecarProcess? sidecar;
        CancellationTokenSource? monitorCancellation;
        Task? monitorTask;
        lock (_lifecycleGate)
        {
            _isStopping = true;
            _isReady = false;
            _monitorGeneration++;
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
            HostOwnerState.Clear();
        }
    }

    /// <summary>
    /// Safety net: a ready DSH must not be listening on a wildcard address.
    /// Spawned sidecars get an explicit loopback host, so a violation means
    /// upstream behavior changed — stop the tree and surface a hard failure
    /// (the safe-mode panel picks it up). Reused endpoints are not stopped
    /// (they are not ours), but they are still policed and refused when not
    /// loopback-only so WebView2 never talks to a LAN-exposed service.
    /// </summary>
    private void EnsureLoopbackBinding()
    {
        if (LoopbackBindingGuard.IsLoopbackOnly(_endpoint.Port))
        {
            return;
        }

        throw new InvalidOperationException(
            $"DSH 监听在非回环地址（端口 {_endpoint.Port}），已拒绝使用以保护本机安全。");
    }

    private async Task RequireLoopbackBindingAsync()
    {
        if (LoopbackBindingGuard.IsLoopbackOnly(_endpoint.Port))
        {
            return;
        }

        await StopAsync();
        throw new InvalidOperationException(
            $"DSH 监听在非回环地址（端口 {_endpoint.Port}），已停止以保护本机安全。" +
            (_logPath is not null ? $"日志：{_logPath}" : string.Empty));
    }

    /// <summary>HTTP 200 plus the Harness shell's root marker.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (await _probe.ProbeAsync(cancellationToken)).IsHealthy;
    }

    private void MarkReadyAndStartMonitoring()
    {
        var cancellation = new CancellationTokenSource();
        int generation;
        lock (_lifecycleGate)
        {
            _isStopping = false;
            _isReady = true;
            _failureReported = false;
            _monitorCancellation?.Cancel();
            _monitorCancellation = cancellation;
            _monitorGeneration++;
            generation = _monitorGeneration;
            _monitorTask = MonitorHealthAsync(generation, cancellation.Token);
        }
    }

    private async Task MonitorHealthAsync(int generation, CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        int consecutiveAuthFailures = 0;
        try
        {
            while (true)
            {
                await Task.Delay(_options.EffectiveMonitorInterval, cancellationToken);

                DshProbeResult probe = await _probe.ProbeAsync(cancellationToken);
                if (probe.IsHealthy)
                {
                    consecutiveFailures = 0;
                    consecutiveAuthFailures = 0;
                    continue;
                }

                if (probe.Status == DshProbeStatus.AuthRejected)
                {
                    // The host is answering; only our cookie is being refused.
                    // A restart cannot repair a credential mismatch — it would
                    // just kill the running agent turn and hit the same 401 —
                    // so this is reported and never acted on. The user sees the
                    // diagnostic panel instead of losing work to a restart loop.
                    consecutiveFailures = 0;
                    consecutiveAuthFailures++;
                    if (consecutiveAuthFailures == 1 || consecutiveAuthFailures % 20 == 0)
                    {
                        RuntimeLog.Append(
                            $"DSH health probe rejected ({probe.Detail}): 主机在响应但会话 Cookie 被拒绝，"
                            + $"保留主机不重启（第 {consecutiveAuthFailures} 次）");
                    }

                    continue;
                }

                consecutiveAuthFailures = 0;
                consecutiveFailures++;
                if (consecutiveFailures < _options.HealthFailureThreshold)
                {
                    continue;
                }

                // One confirmation probe before a kill: a single stalled event
                // loop must not cost a live agent turn.
                DshProbeResult confirmation = await _probe.ProbeAsync(cancellationToken);
                if (confirmation.IsHealthy || !IsMonitoring(generation))
                {
                    consecutiveFailures = 0;
                    continue;
                }

                ReportRuntimeFailure(
                    generation,
                    new DshHostFailureEventArgs(
                        DshHostFailureKind.HealthCheckFailed,
                        null,
                        _logPath,
                        $"连续 {consecutiveFailures} 次探测失败：{confirmation.Detail ?? "无响应"}"));
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop or retry cancelled the active monitor.
        }
    }

    /// <summary>True while <paramref name="generation"/> is still the live monitor.</summary>
    private bool IsMonitoring(int generation)
    {
        lock (_lifecycleGate)
        {
            return _isReady
                && !_isStopping
                && !_failureReported
                && _monitorGeneration == generation;
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
            _monitorGeneration++;
            _sidecar = null;
        }

        RuntimeLog.Append($"DSH process exited: exitCode={e.ExitCode}");
        RuntimeFailure?.Invoke(this, new DshHostFailureEventArgs(
            DshHostFailureKind.ProcessExited,
            e.ExitCode,
            _logPath,
            $"主机进程退出（pid={_observedOwner ?? "未知"}）"));
    }

    /// <summary>Records one failure per monitoring generation.</summary>
    private void ReportRuntimeFailure(int generation, DshHostFailureEventArgs failure)
    {
        RuntimeLog.Append(
            "DSH runtime failure: kind=" + failure.Kind
            + ", exitCode=" + (failure.ExitCode?.ToString() ?? "-")
            + ", log=" + (failure.LogPath ?? "-")
            + (failure.Detail is null ? string.Empty : ", detail=" + failure.Detail));
        lock (_lifecycleGate)
        {
            if (_isStopping
                || !_isReady
                || _failureReported
                || _monitorGeneration != generation)
            {
                return;
            }

            _isReady = false;
            _failureReported = true;
        }

        RuntimeFailure?.Invoke(this, failure);
    }

    private bool IsRecordedOwnerAlive(out string? detail) =>
        HostOwnerState.ReadVerifiedProcessId(out detail) is not null;

    /// <summary>
    /// Test seam: records the running test process as the live owner of the
    /// endpoint, the only state in which Cetus adopts a service it did not
    /// spawn. Mirrors what a second Cetus process finds after a crash.
    /// </summary>
    internal void MarkEndpointOwnedByCurrentProcessForTest()
    {
        using Process current = Process.GetCurrentProcess();
        HostOwnerState.Write(current.Id, current.StartTime);
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
    string? logPath,
    string? detail = null) : EventArgs
{
    public DshHostFailureKind Kind { get; } = kind;

    public int? ExitCode { get; } = exitCode;

    public string? LogPath { get; } = logPath;

    /// <summary>Probe-level diagnosis (status code, timeout, orphan owner).</summary>
    public string? Detail { get; } = detail;
}
