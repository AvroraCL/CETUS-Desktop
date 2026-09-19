using Cetus.Platform;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class CetusProtocolManagerTests
{
    [Fact]
    public void BuildCommand_QuotesExecutableAndPassesUrlArgument() =>
        Assert.Equal(
            @"""C:\Program Files\Cetus\Cetus.exe"" ""%1""",
            CetusProtocolManager.BuildCommand(@"C:\Program Files\Cetus\Cetus.exe"));
}
