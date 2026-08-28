using System.Windows;
using System.Windows.Media.Animation;
using Cetus.Presentation;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class GridLengthAnimationTests
{
    [Fact]
    public void GetCurrentValue_UsesDshSidebarEaseInOut()
    {
        var animation = new GridLengthAnimation
        {
            From = new GridLength(0, GridUnitType.Pixel),
            To = new GridLength(360, GridUnitType.Pixel),
            Duration = TimeSpan.FromMilliseconds(300),
        };
        var clock = (AnimationClock)animation.CreateClock(true);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(
            TimeSpan.FromMilliseconds(150),
            TimeSeekOrigin.BeginTime);

        var midpoint = (GridLength)animation.GetCurrentValue(
            new GridLength(0),
            new GridLength(360),
            clock);

        Assert.Equal(GridUnitType.Pixel, midpoint.GridUnitType);
        Assert.InRange(midpoint.Value, 279, 280);
    }
}
