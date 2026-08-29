using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cetus.Terminal;

/// <summary>
/// Owns one interactive process hosted by Windows ConPTY. The caller writes raw
/// terminal input and renders the VT/ANSI output stream.
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly object _writeSync = new();

    private IntPtr _pty;
    private FileStream? _inputWriter;
    private SafeFileHandle? _outputReadHandle;
    private Process? _process;
    private Task? _readTask;
    private Task? _cleanupTask;
    private int _exitRaised;
    private bool _disposed;

    public event Action<string>? OutputReceived;
    public event EventHandler? Exited;

    internal int ProcessId => _process?.Id ?? 0;

    internal Task CleanupCompleted => _cleanupTask ?? Task.CompletedTask;

    private ConPtySession(
        IntPtr pty,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead)
    {
        _pty = pty;
        _inputWriter = new FileStream(
            inputWrite,
            FileAccess.Write,
            bufferSize: 4096,
            isAsync: false);
        _outputReadHandle = outputRead;
    }

    public static ConPtySession Start(
        string commandLine,
        short columns,
        short rows,
        string? workingDirectory = null,
        Action<string>? outputReceived = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        if (columns <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "终端尺寸必须为正数。");
        }

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        IntPtr pty = IntPtr.Zero;
        ConPtySession? session = null;

        try
        {
            if (!PseudoConsoleInterop.CreatePipe(
                    out inputRead,
                    out inputWrite,
                    IntPtr.Zero,
                    0))
            {
                throw Win32Error("无法创建 PTY 输入管道。");
            }

            if (!PseudoConsoleInterop.CreatePipe(
                    out outputRead,
                    out outputWrite,
                    IntPtr.Zero,
                    0))
            {
                throw Win32Error("无法创建 PTY 输出管道。");
            }

            int result = PseudoConsoleInterop.CreatePseudoConsole(
                new PseudoConsoleInterop.COORD(columns, rows),
                inputRead,
                outputWrite,
                0,
                out pty);
            if (result != 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            session = new ConPtySession(pty, inputWrite, outputRead);
            pty = IntPtr.Zero;
            inputWrite = null;
            outputRead = null;

            if (outputReceived is not null)
            {
                session.OutputReceived += outputReceived;
            }

            session.SpawnProcess(commandLine, workingDirectory);
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;
            session.BeginReadLoop();
            return session;
        }
        catch (COMException error)
        {
            session?.Dispose();
            throw new Win32Exception(
                error.HResult,
                $"无法创建伪终端（ConPTY 需要 Windows 10 1809+）。{error.Message}");
        }
        catch
        {
            session?.Dispose();
            throw;
        }
        finally
        {
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            if (pty != IntPtr.Zero)
            {
                PseudoConsoleInterop.ClosePseudoConsole(pty);
            }
        }
    }

    private void SpawnProcess(string commandLine, string? workingDirectory)
    {
        IntPtr attributeList = IntPtr.Zero;
        IntPtr attributeSize = IntPtr.Zero;
        var processInformation = new PseudoConsoleInterop.PROCESS_INFORMATION();

        try
        {
            bool sizeCallSucceeded = PseudoConsoleInterop.InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref attributeSize);
            if (sizeCallSucceeded || attributeSize == IntPtr.Zero)
            {
                throw Win32Error("无法计算进程属性列表大小。");
            }

            attributeList = Marshal.AllocHGlobal(attributeSize);
            if (!PseudoConsoleInterop.InitializeProcThreadAttributeList(
                    attributeList,
                    1,
                    0,
                    ref attributeSize))
            {
                throw Win32Error("无法初始化进程属性列表。");
            }

            if (!PseudoConsoleInterop.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)PseudoConsoleInterop.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _pty,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw Win32Error("无法绑定伪终端到进程属性。");
            }

            var startupInfo = new PseudoConsoleInterop.STARTUPINFOEX
            {
                StartupInfo =
                {
                    cb = checked((uint)Marshal.SizeOf<PseudoConsoleInterop.STARTUPINFOEX>()),
                    // Prevent a redirected CETUS/test host from leaking its own
                    // stdout/stderr into the child instead of the pseudoconsole.
                    dwFlags = PseudoConsoleInterop.STARTF_USESTDHANDLES,
                },
                lpAttributeList = attributeList,
            };
            var mutableCommandLine = new StringBuilder(commandLine);
            if (!PseudoConsoleInterop.CreateProcess(
                    null,
                    mutableCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    PseudoConsoleInterop.EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out processInformation))
            {
                throw Win32Error("无法启动终端进程。");
            }

            _process = Process.GetProcessById(processInformation.dwProcessId);
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
        }
        finally
        {
            CloseHandle(ref processInformation.hThread);
            CloseHandle(ref processInformation.hProcess);
            if (attributeList != IntPtr.Zero)
            {
                PseudoConsoleInterop.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private void BeginReadLoop()
    {
        SafeFileHandle reader = _outputReadHandle
            ?? throw new InvalidOperationException("终端输出管道尚未建立。");
        _readTask = Task.Run(() =>
        {
            var buffer = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            Decoder decoder = Encoding.UTF8.GetDecoder();

            try
            {
                while (true)
                {
                    bool succeeded = PseudoConsoleInterop.ReadFile(
                        reader,
                        buffer,
                        (uint)buffer.Length,
                        out uint bytesRead,
                        IntPtr.Zero);
                    if (!succeeded)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 109 || _disposed)
                        {
                            break;
                        }

                        throw new Win32Exception(error, "读取伪终端输出失败。");
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    int charCount = decoder.GetChars(
                        buffer.AsSpan(0, checked((int)bytesRead)),
                        chars.AsSpan(),
                        flush: false);
                    if (charCount > 0 && !_disposed)
                    {
                        try
                        {
                            OutputReceived?.Invoke(new string(chars, 0, charCount));
                        }
                        catch
                        {
                            // A renderer callback must not stop output draining.
                        }
                    }
                }
            }
            catch (ObjectDisposedException) when (_disposed)
            {
            }
            catch (Win32Exception) when (_disposed || _process?.HasExited != false)
            {
            }
            finally
            {
                RaiseExited();
            }
        });
    }

    private void OnProcessExited(object? sender, EventArgs e) => RaiseExited();

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Write(string data)
    {
        if (_disposed || string.IsNullOrEmpty(data))
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(data);
        lock (_writeSync)
        {
            if (_disposed || _inputWriter is not { } writer)
            {
                return;
            }

            try
            {
                writer.Write(bytes, 0, bytes.Length);
                writer.Flush();
            }
            catch (IOException) when (_disposed || _process?.HasExited != false)
            {
            }
            catch (ObjectDisposedException) when (_disposed)
            {
            }
        }
    }

    public void Resize(short columns, short rows)
    {
        if (_disposed || _pty == IntPtr.Zero || columns <= 0 || rows <= 0)
        {
            return;
        }

        _ = PseudoConsoleInterop.ResizePseudoConsole(
            _pty,
            new PseudoConsoleInterop.COORD(columns, rows));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_writeSync)
        {
            _inputWriter?.Dispose();
            _inputWriter = null;
        }

        Process? process = _process;
        _process = null;
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        IntPtr pty = _pty;
        _pty = IntPtr.Zero;
        SafeFileHandle? outputReadHandle = _outputReadHandle;
        _outputReadHandle = null;
        Task? readTask = _readTask;
        _readTask = null;

        _cleanupTask = Task.Run(() =>
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process?.Dispose();
            }

            if (pty != IntPtr.Zero)
            {
                PseudoConsoleInterop.ClosePseudoConsole(pty);
            }

            if (readTask is not null)
            {
                try
                {
                    readTask.Wait(2000);
                }
                catch (AggregateException)
                {
                }
            }

            outputReadHandle?.Dispose();
        });
    }

    private static Win32Exception Win32Error(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    private static void CloseHandle(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        PseudoConsoleInterop.CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}
