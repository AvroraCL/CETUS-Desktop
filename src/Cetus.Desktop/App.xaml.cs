using System.Windows;
using Cetus.Application;
using Cetus.Platform;

namespace Cetus;

/// <summary>
/// Application entry: single-instance guard, then the normal StartupUri window.
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstance;
    private CrashReporter? _crashReporter;

    protected override void OnStartup(StartupEventArgs e)
    {
        _crashReporter = CrashReporter.Attach(this);
        _singleInstance = SingleInstanceGuard.AcquireDefault();
        if (!_singleInstance.IsPrimaryInstance)
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
        _singleInstance?.Dispose();
        _singleInstance = null;
        _crashReporter?.Dispose();
        _crashReporter = null;
        base.OnExit(e);
    }
}
