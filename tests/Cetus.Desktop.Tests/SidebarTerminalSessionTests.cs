using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

[Trait("Category", "Integration")]
public sealed class SidebarTerminalSessionTests
{
    [Fact]
    public async Task Session_ExecutesCommandAndReturnsOutput()
    {
        var output = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminal = new SidebarTerminalSession();
        terminal.OutputReceived += (line, isError) =>
        {
            if (!isError && line.Contains("cetus-terminal-ok", StringComparison.Ordinal))
            {
                output.TrySetResult(line);
            }
        };

        terminal.Start();
        terminal.SendCommand("Write-Output 'cetus-terminal-ok'");

        string line = await output.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("cetus-terminal-ok", line, StringComparison.Ordinal);
    }
}
