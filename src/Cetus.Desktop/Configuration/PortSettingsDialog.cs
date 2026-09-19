using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;

namespace Cetus.Configuration;

internal sealed class PortSettingsDialog : Window
{
    private readonly TextBox _portTextBox;
    private readonly TextBlock _validationText;
    private readonly TextBlock _occupancyText;
    private readonly int _effectivePort;

    public PortSettingsDialog(int configuredPort, int effectivePort, bool isOverridden)
    {
        _effectivePort = effectivePort;
        Title = "Cetus · DSH 端口";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 420;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "DSH 仅监听本机回环地址。更改端口后会重新连接 DSH。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock { Text = "此 Cetus 实例的端口：" });

        _portTextBox = new TextBox
        {
            Text = configuredPort.ToString(),
            MinWidth = 160,
            Margin = new Thickness(0, 4, 0, 4),
        };
        panel.Children.Add(_portTextBox);

        string detail = isOverridden
            ? $"当前进程受 CETUS_PORT={effectivePort} 覆盖；移除该环境变量后才使用保存值。"
            : $"当前生效端口：{effectivePort}";
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        _validationText = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Firebrick,
            MinHeight = 20,
        };
        panel.Children.Add(_validationText);

        _occupancyText = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.DarkOrange,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 20,
        };
        panel.Children.Add(_occupancyText);
        _portTextBox.TextChanged += (_, _) => UpdateOccupancyHint();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var save = new Button
        {
            Content = "保存并重连",
            IsDefault = true,
            MinWidth = 100,
        };
        save.Click += OnSave;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) =>
        {
            _portTextBox.Focus();
            _portTextBox.SelectAll();
            UpdateOccupancyHint();
        };
    }

    public int? SelectedPort { get; private set; }

    /// <summary>Warns while the typed port is held by some other listener, so a save does not surprise.</summary>
    private void UpdateOccupancyHint()
    {
        if (!CetusSettings.TryParsePort(_portTextBox.Text, out int port) || port == _effectivePort)
        {
            _occupancyText.Text = string.Empty;
            return;
        }

        bool occupied = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);
        _occupancyText.Text = occupied
            ? "该端口当前正被其他程序监听；保存后 CETUS 会自动改用空闲端口。"
            : string.Empty;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!CetusSettings.TryParsePort(_portTextBox.Text, out int port))
        {
            _validationText.Text = "请输入 1 到 65535 之间的整数端口。";
            return;
        }

        SelectedPort = port;
        DialogResult = true;
    }
}
