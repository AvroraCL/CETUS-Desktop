using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Cetus.Controls;

/// <summary>
/// Renders a Fluent UI System Icons glyph (24dp regular geometry) scaled to
/// <see cref="IconSize"/>, filled with the inherited Foreground so themes and
/// disabled states work without per-usage brushes.
/// </summary>
public sealed class FluentIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(FluentIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FluentIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(FluentIcon),
        new FrameworkPropertyMetadata(
            CreateDefaultForeground(),
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    private static Brush CreateDefaultForeground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        brush.Freeze();
        return brush;
    }

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double size = double.IsNaN(IconSize) ? 16.0 : IconSize;
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Kind is not { Length: > 0 } kind)
        {
            return;
        }

        if (_geometry is null || !_kindRendered.Equals(kind, StringComparison.Ordinal))
        {
            string data = FluentIconPaths.DataOf(kind);
            _geometry = data.Length == 0 ? null : Geometry.Parse(data);
            _geometry?.Freeze();
            _kindRendered = kind;
        }

        if (_geometry is not { } geometry)
        {
            return;
        }

        double scale = IconSize / 24.0;
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        try
        {
            drawingContext.DrawGeometry(Foreground, null, geometry);
        }
        finally
        {
            drawingContext.Pop();
        }
    }

    private string _kindRendered = string.Empty;
    private Geometry? _geometry;
}
