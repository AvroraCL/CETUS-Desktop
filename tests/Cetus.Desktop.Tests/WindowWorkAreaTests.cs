using Cetus.Platform;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class WindowWorkAreaTests
{
    [Fact]
    public void Calculate_ExcludesRightDockedTaskbar()
    {
        MaximizedWindowBounds result = WindowWorkArea.Calculate(
            new NativeBounds(0, 0, 2560, 1440),
            new NativeBounds(0, 0, 2488, 1440));

        Assert.Equal(new MaximizedWindowBounds(0, 0, 2488, 1440), result);
    }

    [Fact]
    public void Calculate_OffsetsForLeftDockedTaskbar()
    {
        MaximizedWindowBounds result = WindowWorkArea.Calculate(
            new NativeBounds(0, 0, 2560, 1440),
            new NativeBounds(72, 0, 2560, 1440));

        Assert.Equal(new MaximizedWindowBounds(72, 0, 2488, 1440), result);
    }

    [Fact]
    public void Calculate_UsesMonitorRelativeCoordinatesOnSecondaryDisplay()
    {
        MaximizedWindowBounds result = WindowWorkArea.Calculate(
            new NativeBounds(-1920, 0, 0, 1080),
            new NativeBounds(-1920, 40, 0, 1080));

        Assert.Equal(new MaximizedWindowBounds(0, 40, 1920, 1040), result);
    }
}
