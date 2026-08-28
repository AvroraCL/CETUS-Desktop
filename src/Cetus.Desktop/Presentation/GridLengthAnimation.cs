using System.Windows;
using System.Windows.Media.Animation;

namespace Cetus.Presentation;

/// <summary>
/// Pixel-only GridLength interpolation used by the native sidebar column.
/// The easing spline is injectable so enter and exit can use different
/// curves (decelerate on expand, accelerate on collapse).
/// </summary>
internal sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly KeySpline DefaultSpline = FreezeSpline(new KeySpline(0.4, 0, 0.2, 1));

    public static readonly DependencyProperty SplineProperty = DependencyProperty.Register(
        nameof(Spline),
        typeof(KeySpline),
        typeof(GridLengthAnimation),
        new PropertyMetadata(DefaultSpline));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From),
        typeof(GridLength),
        typeof(GridLengthAnimation),
        new PropertyMetadata(new GridLength(0)));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To),
        typeof(GridLength),
        typeof(GridLengthAnimation),
        new PropertyMetadata(new GridLength(0)));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    /// <summary>Easing curve; defaults to the DSH-aligned ease-in-out.</summary>
    public KeySpline Spline
    {
        get => (KeySpline)GetValue(SplineProperty);
        set => SetValue(SplineProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    public override object GetCurrentValue(
        object defaultOriginValue,
        object defaultDestinationValue,
        AnimationClock animationClock)
    {
        double progress = animationClock.CurrentProgress ?? 0;
        double easedProgress = Spline.GetSplineProgress(progress);
        double value = From.Value + ((To.Value - From.Value) * easedProgress);
        // Whole-DIP steps: sub-pixel widths invalidate layout and Chromium
        // repaint every tick without moving a device pixel, which shows up as
        // stutter in the slow tail of the ease.
        return new GridLength(Math.Max(0, Math.Round(value)), GridUnitType.Pixel);
    }

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    private static KeySpline FreezeSpline(KeySpline spline)
    {
        spline.Freeze();
        return spline;
    }
}
