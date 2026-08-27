using System.Windows.Threading;
using Cetus.Browser;
using Cetus.Configuration;
using Cetus.Hosting;

namespace Cetus.Runtime;

internal enum DesktopRuntimePhase
{
    Idle,
    StartingHost,
    LoadingBrowser,
    Ready,
    Recovering,
    Failed,
    Stopping,
    Stopped,
}

internal sealed record DesktopRuntimeState(
    DesktopRuntimePhase Phase,
    string Message,
    bool CanRetry,
    Exception? Error = null);

internal sealed class DesktopRuntimeStateChangedEventArgs(DesktopRuntimeState state) : EventArgs
{
    public DesktopRuntimeState State { get; } = state;
}

internal readonly record struct DesktopRuntimeResult(
    bool Succeeded,
    bool WasBusy,
    Exception? Error)
{
    public static DesktopRuntimeResult Success => new(true, false, null);

    public static DesktopRuntimeResult Busy => new(false, true, null);

    public static DesktopRuntimeResult Failed(Exception error) => new(false, false, error);
}

internal readonly record struct PortChangeResult(
    bool Saved,
    bool IsEnvironmentOverridden,
    DesktopRuntimeResult ReconnectResult);

/// <summary>
/// Product runtime state machine. It owns DSH startup, browser connection,
/// automatic recovery, port reconfiguration and ordered shutdown as one module.
/// </summary>
internal sealed class DesktopRuntime
{
    private const int MaxAutomaticRestartAttempts = 3;
    private static readonly TimeSpan StableRuntimeWindow = TimeSpan.FromMinutes(1);

    private readonly CetusSettings _settings;
    private readonly IBrowserSession _browser;
    private readonly Action<Action> _dispatch;
    private readonly IDshHostFactory _hostFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private IDshHost? _host;
    private CancellationTokenSource? _startupCancellation;
    private CancellationTokenSource? _recoveryCancellation;
    private bool _isStarting;
    private bool _isExiting;
    private int _automaticRestartAttempts;
    private DateTimeOffset _lastReadyAt;
    private DshHostFailureEventArgs? _pendingFailure;

    public DesktopRuntime(
        CetusSettings settings,
        BrowserSession browser,
        Dispatcher dispatcher)
        : this(
            settings,
            browser,
            CreateDispatcher(dispatcher),
            new DefaultDshHostFactory(),
            TimeProvider.System,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal DesktopRuntime(
        CetusSettings settings,
        IBrowserSession browser,
        Action<Action> dispatch,
        IDshHostFactory hostFactory,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _settings = settings;
        _browser = browser;
        _dispatch = dispatch;
        _hostFactory = hostFactory;
        _timeProvider = timeProvider;
        _delay = delay;
        State = new DesktopRuntimeState(DesktopRuntimePhase.Idle, "准备启动", CanRetry: true);
    }

    public event EventHandler<DesktopRuntimeStateChangedEventArgs>? StateChanged;

    public DesktopRuntimeState State { get; private set; }

    public bool IsBusy => _isStarting || _isExiting;

    public Uri Endpoint => new($"http://127.0.0.1:{_settings.EffectivePort}/");

    public async Task<DesktopRuntimeResult> StartAsync()
    {
        if (_isStarting || _isExiting)
        {
            return DesktopRuntimeResult.Busy;
        }

        _isStarting = true;
        var cancellation = new CancellationTokenSource();
        _startupCancellation = cancellation;
        try
        {
            _browser.Hide();
            Transition(DesktopRuntimePhase.StartingHost, "正在启动 DSH 主机…", canRetry: false);

            IDshHost host = _host ??= CreateHost();
            await host.StartAsync(cancellation.Token);

            Transition(DesktopRuntimePhase.LoadingBrowser, "正在加载界面…", canRetry: false);
            await _browser.NavigateAsync(Endpoint, cancellation.Token);

            _lastReadyAt = _timeProvider.GetUtcNow();
            Transition(DesktopRuntimePhase.Ready, string.Empty, canRetry: true);
            return DesktopRuntimeResult.Success;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await StopHostAsync();
            string message = _isExiting ? "正在退出…" : "启动已取消";
            Transition(
                _isExiting ? DesktopRuntimePhase.Stopping : DesktopRuntimePhase.Failed,
                message,
                canRetry: !_isExiting);
            return DesktopRuntimeResult.Busy;
        }
        catch (Exception error)
        {
            await StopHostAsync();
            Transition(DesktopRuntimePhase.Failed, "启动失败", canRetry: true, error);
            return DesktopRuntimeResult.Failed(error);
        }
        finally
        {
            if (ReferenceEquals(_startupCancellation, cancellation))
            {
                _startupCancellation = null;
            }
            cancellation.Dispose();
            _isStarting = false;

            if (!State.CanRetry
                && State.Phase is not DesktopRuntimePhase.Stopping
                and not DesktopRuntimePhase.Stopped)
            {
                Transition(State with { CanRetry = true });
            }

            if (_pendingFailure is { } failure
                && !_isExiting
                && State.Phase == DesktopRuntimePhase.Ready)
            {
                _pendingFailure = null;
                BeginAutomaticRecovery(failure);
            }
        }
    }

    public async Task<DesktopRuntimeResult> RetryAsync(
        bool isAutomatic = false,
        bool recreateHost = false)
    {
        if (_isExiting)
        {
            return DesktopRuntimeResult.Busy;
        }

        if (_isStarting)
        {
            if (!isAutomatic)
            {
                CancelStartup();
            }
            return DesktopRuntimeResult.Busy;
        }

        if (!isAutomatic)
        {
            _automaticRestartAttempts = 0;
            CancelAutomaticRecovery();
        }

        _browser.Hide();
        await StopHostAsync();
        if (recreateHost)
        {
            DiscardHost();
        }
        return await StartAsync();
    }

    public async Task<PortChangeResult> ChangePortAsync(int port)
    {
        if (_isStarting || _isExiting)
        {
            return new PortChangeResult(false, _settings.IsPortOverridden, DesktopRuntimeResult.Busy);
        }

        try
        {
            _settings.SetConfiguredPort(port);
        }
        catch (Exception error)
        {
            return new PortChangeResult(false, _settings.IsPortOverridden, DesktopRuntimeResult.Failed(error));
        }

        if (_settings.IsPortOverridden)
        {
            return new PortChangeResult(true, true, DesktopRuntimeResult.Success);
        }

        DesktopRuntimeResult reconnect = await RetryAsync(recreateHost: true);
        return new PortChangeResult(true, false, reconnect);
    }

    public async Task StopAsync()
    {
        if (State.Phase == DesktopRuntimePhase.Stopped)
        {
            return;
        }

        _isExiting = true;
        CancelStartup();
        CancelAutomaticRecovery();
        Transition(DesktopRuntimePhase.Stopping, "正在退出…", canRetry: false);
        _browser.Hide();
        await StopHostAsync();
        DiscardHost();
        Transition(DesktopRuntimePhase.Stopped, "已停止", canRetry: false);
    }

    private IDshHost CreateHost()
    {
        IDshHost host = _hostFactory.Create(Endpoint, _settings.DshHomeOverride);
        host.RuntimeFailure += OnHostRuntimeFailure;
        return host;
    }

    private async Task StopHostAsync()
    {
        if (_host is { } host)
        {
            await host.StopAsync();
        }
    }

    private void DiscardHost()
    {
        if (_host is { } host)
        {
            host.RuntimeFailure -= OnHostRuntimeFailure;
            host.Dispose();
            _host = null;
        }
    }

    private void OnHostRuntimeFailure(object? sender, DshHostFailureEventArgs failure)
    {
        if (_isExiting || !ReferenceEquals(sender, _host))
        {
            return;
        }

        if (_isStarting)
        {
            _pendingFailure = failure;
            return;
        }

        _dispatch(() => BeginAutomaticRecovery(failure));
    }

    private void BeginAutomaticRecovery(DshHostFailureEventArgs failure)
    {
        if (_isExiting || _isStarting)
        {
            return;
        }

        CancelAutomaticRecovery();
        var cancellation = new CancellationTokenSource();
        _recoveryCancellation = cancellation;
        _ = RecoverFromRuntimeFailureAsync(failure, cancellation);
    }

    private async Task RecoverFromRuntimeFailureAsync(
        DshHostFailureEventArgs failure,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (_timeProvider.GetUtcNow() - _lastReadyAt >= StableRuntimeWindow)
            {
                _automaticRestartAttempts = 0;
            }

            while (_automaticRestartAttempts < MaxAutomaticRestartAttempts)
            {
                int attempt = ++_automaticRestartAttempts;
                int delaySeconds = attempt * 2;
                _browser.Hide();
                string failureDescription = failure.Kind == DshHostFailureKind.ProcessExited
                    ? $"DSH 主机意外退出（代码 {failure.ExitCode?.ToString() ?? "未知"}）"
                    : "DSH 服务连续健康检查失败";
                Transition(
                    DesktopRuntimePhase.Recovering,
                    $"{failureDescription}；将在 {delaySeconds} 秒后尝试恢复（{attempt}/{MaxAutomaticRestartAttempts}）…",
                    canRetry: true);

                await _delay(TimeSpan.FromSeconds(delaySeconds), cancellation.Token);
                if (_isExiting || cancellation.IsCancellationRequested)
                {
                    return;
                }

                DesktopRuntimeResult result = await RetryAsync(isAutomatic: true);
                if (result.Succeeded)
                {
                    return;
                }
            }

            if (!_isExiting && !cancellation.IsCancellationRequested)
            {
                Transition(
                    DesktopRuntimePhase.Failed,
                    "DSH 多次启动失败，已停止自动恢复。请在托盘菜单中选择“重试连接 DSH”。",
                    canRetry: true,
                    State.Error);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Manual retry or application exit cancelled this recovery chain.
        }
        finally
        {
            if (ReferenceEquals(_recoveryCancellation, cancellation))
            {
                _recoveryCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelStartup() => _startupCancellation?.Cancel();

    private void CancelAutomaticRecovery()
    {
        CancellationTokenSource? cancellation = _recoveryCancellation;
        _recoveryCancellation = null;
        cancellation?.Cancel();
    }

    private void Transition(
        DesktopRuntimePhase phase,
        string message,
        bool canRetry,
        Exception? error = null) =>
        Transition(new DesktopRuntimeState(phase, message, canRetry, error));

    private void Transition(DesktopRuntimeState state)
    {
        State = state;
        StateChanged?.Invoke(this, new DesktopRuntimeStateChangedEventArgs(state));
    }

    private static Action<Action> CreateDispatcher(Dispatcher dispatcher) =>
        action =>
        {
            if (!dispatcher.HasShutdownStarted)
            {
                _ = dispatcher.InvokeAsync(action);
            }
        };
}
