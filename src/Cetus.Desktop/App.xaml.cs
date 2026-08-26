using System.Threading;
using System.Windows;

namespace Cetus;

/// <summary>
/// Application entry: single-instance guard, then the normal StartupUri window.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Cetus.Desktop.SingleInstance";

    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
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
