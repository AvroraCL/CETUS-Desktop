using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Cetus.Configuration;

namespace Cetus.Platform;

/// <summary>
/// Records otherwise unhandled shell failures without changing the platform's
/// termination decision. The sidecar Job Object remains responsible for cleanup.
/// </summary>
internal sealed class CrashReporter : IDisposable
{
    private readonly System.Windows.Application _application;
    private readonly object _writeGate = new();
    private bool _disposed;

    private CrashReporter(System.Windows.Application application)
    {
        _application = application;
        _application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static CrashReporter Attach(System.Windows.Application application) => new(application);

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e) =>
        Write("WPF dispatcher", e.Exception);

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception error = e.ExceptionObject as Exception
            ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown unhandled error");
        Write(e.IsTerminating ? "AppDomain terminating" : "AppDomain", error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write("Unobserved task", e.Exception);
        e.SetObserved();
    }

    private void Write(string source, Exception error)
    {
        try
        {
            lock (_writeGate)
            {
                Directory.CreateDirectory(CetusPaths.LogDirectory);
                string path = Path.Combine(CetusPaths.LogDirectory, "cetus-crash.log");
                var entry = new StringBuilder()
                    .AppendLine("============================================================")
                    .AppendLine($"UTC: {DateTimeOffset.UtcNow:O}")
                    .AppendLine($"Source: {source}")
                    .AppendLine($"Process: {Environment.ProcessPath}")
                    .AppendLine($"OS: {Environment.OSVersion}")
                    .AppendLine($"Runtime: {Environment.Version}")
                    .AppendLine(error.ToString())
                    .ToString();
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // Crash reporting must never replace the original exception.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }
}
