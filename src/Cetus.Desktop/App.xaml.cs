using System.Threading;
using System.Windows;

namespace Cetus;

/// <summary>
/// Application entry: single-instance guard, then the normal StartupUri window.
/// </summary>
public partial class App : Application
{
    private const string MutexNamePrefix = @"Local\Cetus.Desktop.SingleInstance";

    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // CETUS_INSTANCE_ID lets isolated development checks run beside a
        // normally installed Cetus without colliding on the single-instance guard.
        string instanceId = Environment.GetEnvironmentVariable("CETUS_INSTANCE_ID") ?? string.Empty;
        string suffix = string.IsNullOrWhiteSpace(instanceId) ? string.Empty : $".{instanceId.Trim()}";
        _mutex = new Mutex(initiallyOwned: true, MutexNamePrefix + suffix, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show(
                "Cetus 已经在运行了。",
                "Cetus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
