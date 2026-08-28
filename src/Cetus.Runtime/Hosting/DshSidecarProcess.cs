using System.Diagnostics;
using System.IO;

namespace Cetus.Hosting;

internal sealed class DshSidecarExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Owns exactly one spawned DSH process tree, Windows Job Object and sidecar
/// log. Cleanup is idempotent across explicit stop and asynchronous exit.
/// </summary>
internal sealed class DshSidecarProcess
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _logWriteGate = new(1, 1);
    private Process? _process;
    private SidecarJob? _job;
    private FileStream? _logStream;
    private int? _exitCode;
    private bool _stopping;

    private DshSidecarProcess(
        Process process,
        SidecarJob job,
        FileStream logStream,
        string logPath)
    {
        _process = process;
        _job = job;
        _logStream = logStream;
        LogPath = logPath;
    }

    public event EventHandler<DshSidecarExitedEventArgs>? Exited;

    public string LogPath { get; }

    public static DshSidecarProcess Start(
        DshCommand command,
        Uri endpoint,
        string? dshHomeOverride,
        EventHandler<DshSidecarExitedEventArgs> exited)
    {
        ProcessStartInfo startInfo = CreateStartInfo(command, endpoint, dshHomeOverride);
        string logDirectory = Cetus.Configuration.CetusPaths.LogDirectory;
        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, $"dsh-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var logStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        Process? process = null;
        SidecarJob? job = null;
        try
        {
            job = SidecarJob.Create();
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 DSH 主机进程。");
            job.Assign(process);

            var sidecar = new DshSidecarProcess(process, job, logStream, logPath);
            process = null;
            job = null;
            logStream = null!;

            sidecar.Exited += exited;
            Process ownedProcess = sidecar._process!;
            Stream standardOutput = ownedProcess.StandardOutput.BaseStream;
            Stream standardError = ownedProcess.StandardError.BaseStream;
            ownedProcess.Exited += sidecar.OnProcessExited;
            ownedProcess.EnableRaisingEvents = true;
            _ = sidecar.PumpAsync(standardOutput);
            _ = sidecar.PumpAsync(standardError);
            return sidecar;
        }
        catch
        {
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
            job?.Dispose();
            logStream?.Dispose();
            throw;
        }
    }

    public bool TryGetExitCode(out int? exitCode)
    {
        lock (_gate)
        {
            if (_exitCode is not null)
            {
                exitCode = _exitCode;
                return true;
            }

            if (_process is null)
            {
                exitCode = null;
                return false;
            }

            try
            {
                if (_process.HasExited)
                {
                    _exitCode = _process.ExitCode;
                    exitCode = _exitCode;
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // The process has not started or has already been released.
            }

            exitCode = null;
            return false;
        }
    }

    public async Task StopAsync()
    {
        Process? process;
        SidecarJob? job;
        FileStream? logStream;
        lock (_gate)
        {
            _stopping = true;
            process = _process;
            _process = null;
            job = _job;
            _job = null;
            logStream = _logStream;
            _logStream = null;
        }

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                _exitCode ??= process.ExitCode;
            }
            catch
            {
                // Closing the job below remains the authoritative tree cleanup.
            }
            finally
            {
                process.Dispose();
            }
        }

        job?.Dispose();
        logStream?.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            return;
        }

        SidecarJob? job;
        FileStream? logStream;
        lock (_gate)
        {
            if (_stopping || !ReferenceEquals(process, _process))
            {
                return;
            }

            _exitCode = exitCode;
            _process = null;
            job = _job;
            _job = null;
            logStream = _logStream;
            _logStream = null;
        }

        process.Exited -= OnProcessExited;
        process.Dispose();
        job?.Dispose();
        logStream?.Dispose();
        Exited?.Invoke(this, new DshSidecarExitedEventArgs(exitCode));
    }

    private async Task PumpAsync(Stream source)
    {
        try
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await _logWriteGate.WaitAsync();
                try
                {
                    FileStream? sink;
                    lock (_gate)
                    {
                        sink = _logStream;
                    }
                    if (sink is null)
                    {
                        return;
                    }
                    await sink.WriteAsync(buffer.AsMemory(0, read));
                    await sink.FlushAsync();
                }
                finally
                {
                    _logWriteGate.Release();
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    private static ProcessStartInfo CreateStartInfo(
        DshCommand command,
        Uri endpoint,
        string? dshHomeOverride)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (!string.IsNullOrWhiteSpace(dshHomeOverride))
        {
            startInfo.Environment["DSH_HOME"] = dshHomeOverride;
        }

        if (command.UseShim)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("dsh");
            startInfo.ArgumentList.Add("web");
            startInfo.ArgumentList.Add("--no-open");
        }
        else
        {
            startInfo.FileName = command.NodeExe!;
            startInfo.ArgumentList.Add(command.EntryScript!);
            startInfo.ArgumentList.Add("web");
            startInfo.ArgumentList.Add("--no-open");
        }

        if (endpoint.Port != Cetus.Configuration.CetusSettings.DefaultPort)
        {
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(endpoint.Port.ToString());
        }
        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // The job handle remains the final cleanup path.
        }
    }
}
