using System.IO;
using System.Windows;
using Cetus.Desktop.Core;

namespace Cetus.Desktop;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(CetusPaths.LogDir, "cetus-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // M0: single window. Tray, single-instance mutex and auto-restart are M1.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }

    // Unhandled UI-thread exceptions land in a log file instead of a silent exit.
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the app further
        }
    }
}
