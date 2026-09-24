using System.Windows;
using System.Windows.Controls;
using Cetus.Configuration;

namespace Cetus.Configuration;

/// <summary>
/// Native CETUS settings dialog, reachable from the tray even when the DSH
/// page (and therefore the injected settings group) is unavailable. Mirrors
/// the toggles injected into the Harness settings page; changes persist the
/// same way and take effect immediately.
/// </summary>
internal sealed class CetusSettingsDialog : Window
{
    /// <summary>Raised after any toggle changes so the owner can apply
    /// live-behavior settings (e.g. hotkey registration) immediately.</summary>
    public event EventHandler? SettingChanged;

    public CetusSettingsDialog(CetusSettings settings)
    {
        Title = "Cetus · CETUS设置";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 460;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "这些开关与 DSH 设置页「CETUS设置」分组相同；DSH 离线时也可在此修改。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(MakeToggle("启动时检查更新", "启动 CETUS 时自动检测新版本",
            settings.CheckUpdatesOnStartup, changed => OnToggle(settings, changed)));
        panel.Children.Add(MakeToggle("任务完成提醒", "会话不在前台时，任务完成弹出托盘通知",
            settings.NotifyOnAgentComplete, changed => OnToggle(settings, changed)));
        panel.Children.Add(MakeToggle("全局快捷键", "Ctrl+Alt+Space 随时唤起或隐藏 CETUS",
            settings.GlobalHotkeyEnabled, changed => OnToggle(settings, changed)));
        panel.Children.Add(MakeToggle("开机自启", "登录 Windows 后 CETUS 在后台启动并驻留托盘",
            Cetus.Platform.AutostartManager.IsEnabled(), changed => Cetus.Platform.AutostartManager.SetEnabled(changed)));
        panel.Children.Add(MakeToggle("关闭按钮最小化到托盘", "开启时点关闭按钮驻留托盘，关闭则直接退出",
            settings.CloseToTray, changed => OnToggle(settings, changed)));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var close = new Button { Content = "关闭", IsCancel = true, MinWidth = 80, Padding = new Thickness(16, 5, 16, 5) };
        buttons.Children.Add(close);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private void OnToggle(CetusSettings settings, bool changed)
    {
        SettingChanged?.Invoke(this, EventArgs.Empty);
    }

    private static StackPanel MakeToggle(
        string title, string description, bool initial, Action<bool> onChanged)
    {
        var row = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new CheckBox { IsChecked = initial, VerticalAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) => onChanged(true);
        box.Unchecked += (_, _) => onChanged(false);
        header.Children.Add(box);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(header);
        row.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = System.Windows.Media.Brushes.DimGray,
            FontSize = 12,
            Margin = new Thickness(24, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }
}
