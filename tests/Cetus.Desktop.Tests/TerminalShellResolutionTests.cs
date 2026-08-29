using Cetus.Sidebar;
using Cetus.Terminal;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class TerminalShellResolutionTests
{
    [Fact]
    public void Resolver_PrefersPowerShell7AndQuotesSpaces()
    {
        string command = TerminalTabContent.ResolveShellCommandLine();

        Assert.False(string.IsNullOrWhiteSpace(command));
        Assert.Contains("pwsh.exe", command, StringComparison.OrdinalIgnoreCase);
        if (command.Contains(' '))
        {
            Assert.StartsWith("\"", command, StringComparison.Ordinal);
            Assert.EndsWith("\"", command, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ResolvedShell_ShowsPwshVersionBanner()
    {
        string command = TerminalTabContent.ResolveShellCommandLine();
        var output = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminal = ConPtySession.Start(
            command,
            100,
            30,
            outputReceived: chunk =>
            {
                if (chunk.Contains("PowerShell 7.", StringComparison.Ordinal))
                {
                    output.TrySetResult();
                }
            });

        // Profile loading (oh-my-posh 等) can take a few seconds.
        await output.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
