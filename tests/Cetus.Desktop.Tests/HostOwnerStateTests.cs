using System.Diagnostics;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// The ownership marker is what stops Cetus from adopting a DSH host that a
/// crashed previous Cetus process left behind — such a host answers health
/// checks perfectly and then dies with its old Job Object, which the user sees
/// as "the host restarted by itself right after launch".
/// </summary>
public sealed class HostOwnerStateTests
{
    [Fact]
    public void ReadVerifiedProcessId_ReturnsLiveProcess()
    {
        using var environment = new UserDataScope();
        using Process current = Process.GetCurrentProcess();

        HostOwnerState.Write(current.Id, current.StartTime);

        int? verified = HostOwnerState.ReadVerifiedProcessId(out string? detail);
        Assert.Equal(current.Id, verified);
        Assert.NotNull(detail);
    }

    [Fact]
    public void ReadVerifiedProcessId_RejectsDeadProcess()
    {
        using var environment = new UserDataScope();
        int deadPid = StartAndExitProcess();

        HostOwnerState.Write(deadPid, DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.Null(HostOwnerState.ReadVerifiedProcessId(out string? detail));
        Assert.Contains(deadPid.ToString(), detail);
    }

    [Fact]
    public void ReadVerifiedProcessId_RejectsRecycledPid()
    {
        using var environment = new UserDataScope();
        using Process current = Process.GetCurrentProcess();

        // Same pid, but a start time that cannot belong to this process.
        HostOwnerState.Write(current.Id, current.StartTime.AddHours(-3));

        Assert.Null(HostOwnerState.ReadVerifiedProcessId(out string? detail));
        Assert.NotNull(detail);
    }

    [Fact]
    public void Clear_RemovesTheMarker()
    {
        using var environment = new UserDataScope();
        HostOwnerState.Write(Environment.ProcessId, DateTimeOffset.UtcNow);
        Assert.True(File.Exists(HostOwnerState.FilePath));

        HostOwnerState.Clear();

        Assert.False(File.Exists(HostOwnerState.FilePath));
        Assert.Null(HostOwnerState.ReadProcessId());
    }

    private static int StartAndExitProcess()
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        int pid = process.Id;
        process.WaitForExit();
        return pid;
    }

    private sealed class UserDataScope : IDisposable
    {
        private readonly string _directory = TestWorkspace.CreateDirectory();
        private readonly string? _original = Environment.GetEnvironmentVariable("CETUS_USER_DATA_DIR");
        private readonly string? _originalLogDir = Environment.GetEnvironmentVariable("CETUS_LOG_DIR");

        public UserDataScope()
        {
            Environment.SetEnvironmentVariable("CETUS_USER_DATA_DIR", _directory);
            Environment.SetEnvironmentVariable("CETUS_LOG_DIR", Path.Combine(_directory, "logs"));
            HostOwnerState.Clear();
        }

        public void Dispose()
        {
            HostOwnerState.Clear();
            Environment.SetEnvironmentVariable("CETUS_USER_DATA_DIR", _original);
            Environment.SetEnvironmentVariable("CETUS_LOG_DIR", _originalLogDir);
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
