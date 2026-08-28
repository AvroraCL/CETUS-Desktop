using System.Diagnostics;
using System.IO;
using System.Text;

namespace Cetus.Sidebar;

public sealed class SidebarTerminalSession : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private bool _disposed;

    public event Action<string, bool>? OutputReceived;

    public event Action? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NoExit -Command -",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            process.Exited += OnProcessExited;
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 PowerShell。\n");
            }

            _process = process;
            _ = PumpAsync(process.StandardOutput, isError: false);
            _ = PumpAsync(process.StandardError, isError: true);
            process.StandardInput.WriteLine(
                "$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)");
            process.StandardInput.Flush();
        }
    }

    public void SendCommand(string command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        lock (_sync)
        {
            if (_process is not { HasExited: false } process)
            {
                throw new InvalidOperationException("终端尚未启动。");
            }

            OutputReceived?.Invoke($"> {command}", false);
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
        }
    }

    private async Task PumpAsync(StreamReader reader, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                OutputReceived?.Invoke(line, isError);
            }
        }
        catch (ObjectDisposedException)
        {
            // The owning window is shutting down.
        }
        catch (IOException)
        {
            // The redirected pipe closes when PowerShell exits.
        }
    }

    private void OnProcessExited(object? sender, EventArgs e) => Exited?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        process.Exited -= OnProcessExited;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("exit");
                process.StandardInput.Close();
                if (!process.WaitForExit(750))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and shutdown request.
        }
        finally
        {
            process.Dispose();
        }
    }
}
