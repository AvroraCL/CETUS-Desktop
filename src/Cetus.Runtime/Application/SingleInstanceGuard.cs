using System.Threading;

namespace Cetus.Application;

/// <summary>
/// Owns the process-wide single-instance mutex. CETUS_INSTANCE_ID creates an
/// explicitly isolated identity for development and smoke-test launches.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexNamePrefix = @"Local\Cetus.Desktop.SingleInstance";

    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public static SingleInstanceGuard AcquireDefault() =>
        Acquire(Environment.GetEnvironmentVariable("CETUS_INSTANCE_ID"));

    public static SingleInstanceGuard Acquire(string? instanceId)
    {
        string suffix = string.IsNullOrWhiteSpace(instanceId)
            ? string.Empty
            : $".{instanceId.Trim()}";
        var mutex = new Mutex(
            initiallyOwned: true,
            MutexNamePrefix + suffix,
            out bool ownsMutex);
        return new SingleInstanceGuard(mutex, ownsMutex);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
