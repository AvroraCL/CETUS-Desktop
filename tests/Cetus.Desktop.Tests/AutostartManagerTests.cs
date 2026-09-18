using Cetus.Platform;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class AutostartManagerTests
{
    [Fact]
    public void BuildRunValue_QuotesPathAndAppendsBackgroundFlag()
    {
        string value = AutostartManager.BuildRunValue(@"C:\Program Files\Cetus\Cetus.exe");

        Assert.Equal(@"""C:\Program Files\Cetus\Cetus.exe"" --background", value);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Cetus\Cetus.exe"" --background", @"C:\Program Files\Cetus\Cetus.exe")]
    [InlineData(@"""C:\Apps\Cetus.exe""", @"C:\Apps\Cetus.exe")]
    [InlineData(@"C:\Apps\Cetus.exe --background", @"C:\Apps\Cetus.exe")]
    [InlineData(@"C:\Apps\Cetus.exe", @"C:\Apps\Cetus.exe")]
    public void ExtractExecutablePath_ParsesQuotedAndBareValues(string runValue, string expected) =>
        Assert.Equal(expected, AutostartManager.ExtractExecutablePath(runValue));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"""unterminated")]
    public void ExtractExecutablePath_RejectsMalformedValues(string runValue) =>
        Assert.Null(AutostartManager.ExtractExecutablePath(runValue));
}
