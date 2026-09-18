using System.Windows;
using Cetus.Application;
using Cetus.Platform;

namespace Cetus;

/// <summary>
/// Application entry: single-instance guard, then a centered brand splash
/// while the DSH host starts; the main window appears once it settles.
/// `--background` (autostart) skips the splash entirely and keeps the main
/// window hidden — the tray icon and a prewarmed DSH host are the whole UI.
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstance;
    private CrashReporter? _crashReporter;
    private SplashWindow? _splash;
    private MainWindow? _mainWindow;

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

        bool startInBackground = e.Args.Any(arg =>
            arg.Equals("--background", StringComparison.OrdinalIgnoreCase));
        if (!startInBackground)
        {
            _splash = new SplashWindow();
            _splash.Show();
        }

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.SplashDismissRequested += (_, _) => DismissSplash();
        _mainWindow.StartStartup(startInBackground);
    }

    private void DismissSplash()
    {
        if (_splash is null)
        {
            return;
        }

        _splash.Close();
        _splash = null;
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
