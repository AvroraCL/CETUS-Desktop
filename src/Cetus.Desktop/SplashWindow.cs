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
        Width = 160;
        Height = 160;

        var icon = new Image
        {
            Width = 120,
            Height = 120,
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/cetus-splash.png")),
        };
        icon.Effect = new DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 0,
            Opacity = 0.45,
        };
        Content = icon;
    }
}
