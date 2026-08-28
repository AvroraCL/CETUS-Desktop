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
        var midpoint = SampleAt(animation, TimeSpan.FromMilliseconds(150));

        Assert.Equal(GridUnitType.Pixel, midpoint.GridUnitType);
        Assert.InRange(midpoint.Value, 279, 280);
    }

    [Fact]
    public void GetCurrentValue_ExpandSpline_DeceleratesIntoPlace()
    {
        // At any instant before settle the decelerate curve (expand) is ahead
        // of the accelerate curve (collapse) — fast response, gentle settle.
        var expand = new GridLengthAnimation
        {
            From = new GridLength(0, GridUnitType.Pixel),
            To = new GridLength(360, GridUnitType.Pixel),
            Duration = TimeSpan.FromMilliseconds(240),
            Spline = new KeySpline(0, 0, 0.2, 1),
        };
        var collapse = new GridLengthAnimation
        {
            From = new GridLength(0, GridUnitType.Pixel),
            To = new GridLength(360, GridUnitType.Pixel),
            Duration = TimeSpan.FromMilliseconds(240),
            Spline = new KeySpline(0.4, 0, 1, 1),
        };

        foreach (var time in new[] { 60, 120, 180 })
        {
            var expandValue = SampleAt(expand, TimeSpan.FromMilliseconds(time)).Value;
            var collapseValue = SampleAt(collapse, TimeSpan.FromMilliseconds(time)).Value;
            Assert.True(
                expandValue > collapseValue,
                $"at {time}ms expand ({expandValue}) should lead collapse ({collapseValue})");
        }
    }

    private static GridLength SampleAt(GridLengthAnimation animation, TimeSpan time)
    {
        var clock = (AnimationClock)animation.CreateClock(true);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(time, TimeSeekOrigin.BeginTime);
        return (GridLength)animation.GetCurrentValue(
            new GridLength(0),
            new GridLength(360),
            clock);
    }
}
