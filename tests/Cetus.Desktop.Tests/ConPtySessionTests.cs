using System.Diagnostics;
using System.Text;
using Cetus.Terminal;
using Xunit;

namespace Cetus.Desktop.Tests;

[Trait("Category", "Integration")]
public sealed class ConPtySessionTests
{
    [Fact]
    public async Task Session_CapturesOneShotConsoleOutput()
    {
        var output = new StringBuilder();
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminal = ConPtySession.Start(
            "cmd.exe /d /s /c \"echo cetus-conpty-oneshot\"",
            80,
            24,
            outputReceived: chunk =>
            {
                lock (output)
                {
                    output.Append(chunk);
                    if (output.ToString().Contains("cetus-conpty-oneshot", StringComparison.Ordinal))
                    {
                        completed.TrySetResult();
                    }
                }
            });

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Session_StreamsInteractivePowerShellOutputAndAnsi()
    {
        var output = new StringBuilder();
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminal = ConPtySession.Start(
            "powershell.exe -NoLogo -NoProfile -NoExit",
            80,
            24,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            chunk =>
            {
                lock (output)
                {
                    output.Append(chunk);
                    if (output.ToString().Contains("cetus-conpty-ok", StringComparison.Ordinal))
                    {
                        completed.TrySetResult();
                    }
                }
            });

        terminal.Resize(100, 30);
        terminal.Write("Write-Host 'cetus-conpty-ok' -ForegroundColor Green\r");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        string captured;
        lock (output)
        {
            captured = output.ToString();
        }

        Assert.Contains("cetus-conpty-ok", captured, StringComparison.Ordinal);
        Assert.Contains("\u001b[", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_ReturnsWithoutWaitingForNativeCleanup()
    {
        var terminal = ConPtySession.Start(
            "powershell.exe -NoLogo -NoProfile -NoExit",
            80,
            24);
        var stopwatch = Stopwatch.StartNew();

        terminal.Dispose();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await terminal.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_StopsHostedProcess()
    {
        var terminal = ConPtySession.Start(
            "powershell.exe -NoLogo -NoProfile -NoExit",
            80,
            24);
        int processId = terminal.ProcessId;

        terminal.Dispose();
        await terminal.CleanupCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(
            () => !ProcessExists(processId),
            TimeSpan.FromSeconds(5));
        Assert.False(ProcessExists(processId));
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}
