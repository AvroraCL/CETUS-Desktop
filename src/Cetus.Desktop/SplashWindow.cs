using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace Cetus;

/// <summary>
/// Borderless centered splash showing the brand icon while the DSH host
/// starts. No taskbar button, never activates, closes the moment the main
/// window is ready to appear.
/// </summary>
public sealed class SplashWindow : Window
{
    public SplashWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 320;
        Height = 320;

        var icon = new Image
        {
            Width = 240,
            Height = 240,
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/cetus-splash.png")),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        icon.Effect = new DropShadowEffect
        {
            BlurRadius = 28,
            ShadowDepth = 0,
            Opacity = 0.45,
        };
        Content = icon;

        // Gentle breathing pulse so the splash reads as "loading", not frozen.
        var pulse = new DoubleAnimation
        {
            From = 0.96,
            To = 1.06,
            Duration = TimeSpan.FromMilliseconds(900),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        var transform = (ScaleTransform)icon.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse.Clone());
    }
}
