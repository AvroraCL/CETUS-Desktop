using System.Windows;
using System.Windows.Threading;
using Cetus.Application;
using Cetus.Configuration;
using Cetus.Platform;

namespace Cetus;

/// <summary>
/// Application entry: single-instance guard, then a centered brand splash
/// while the DSH host starts; the main window appears once it settles.
/// `--background` (autostart) skips the splash entirely and keeps the main
/// window hidden — the tray icon and a prewarmed DSH host are the whole UI.
/// A workspace directory argument (or cetus:// link) opens a session there;
/// when an instance already runs, the request is forwarded over the
/// activation pipe instead of starting a second copy.
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstance;
    private CrashReporter? _crashReporter;
    private SplashWindow? _splash;
    private MainWindow? _mainWindow;
    private ActivationChannel? _activationChannel;

    protected override void OnStartup(StartupEventArgs e)
    {
        _crashReporter = CrashReporter.Attach(this);
        string? instanceId = Environment.GetEnvironmentVariable("CETUS_INSTANCE_ID");
        _singleInstance = SingleInstanceGuard.Acquire(instanceId);
        LaunchRequest launch = LaunchArgs.Parse(e.Args);
        if (!_singleInstance.IsPrimaryInstance)
        {
            if (!ActivationChannel.TryForwardAsync(launch.WorkspacePath, instanceId)
                .GetAwaiter().GetResult())
            {
                MessageBox.Show(
                    "Cetus 已经在运行了。",
                    "Cetus",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        if (!DevModeFlag.IsActive)
        {
            CetusProtocolManager.EnsureRegistered();
        }

        _activationChannel = new ActivationChannel(
            instanceId,
            request => Dispatcher.BeginInvoke(
                () => _mainWindow?.ActivateWorkspace(request.WorkspacePath),
                DispatcherPriority.Normal));
        _activationChannel.Start();

        bool startInBackground = launch.StartInBackground;
        if (!startInBackground)
        {
            _splash = new SplashWindow();
            _splash.Show();
        }

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.SplashDismissRequested += (_, _) => DismissSplash();
        _mainWindow.StartStartup(startInBackground);
        if (launch.WorkspacePath is { } workspacePath)
        {
            _mainWindow.ActivateWorkspace(workspacePath);
        }
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
        _activationChannel?.Dispose();
        _activationChannel = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        _crashReporter?.Dispose();
        _crashReporter = null;
        base.OnExit(e);
    }
}
