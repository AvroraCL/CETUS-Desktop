using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cetus.Updates;
using Brushes = System.Windows.Media.Brushes;

namespace Cetus.Updates;

/// <summary>
/// Owner-drawn update prompt following the PortSettingsDialog pattern. The
/// dialog owns no network logic: the coordinator subscribes to its events and
/// reports progress back through the methods below.
/// </summary>
internal sealed class UpdatePromptDialog : Window
{
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progress;
    private readonly Button _installButton;
    private readonly Button _releasesButton;
    private readonly Button _dismissButton;
    private readonly bool _installedEdition;
    private bool _downloading;

    /// <summary>Raised when the user asks for the update to be installed.</summary>
    public event Action? InstallClicked;

    /// <summary>Raised when the user asks to open the releases page instead.</summary>
    public event Action? OpenReleasesClicked;

    /// <summary>Raised when the user cancels an in-flight download.</summary>
    public event Action? CancelClicked;

    public UpdatePromptDialog(string currentVersion, ReleaseInfo release, bool installedEdition)
    {
        _installedEdition = installedEdition;

        Title = "CETUS · 更新";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 460;
        MaxWidth = 560;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = $"发现新版本 {release.TagName}（当前 {currentVersion}）",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        string notesText = string.IsNullOrWhiteSpace(release.Notes)
            ? "（发布未附更新说明）"
            : release.Notes;
        var notesScroll = new ScrollViewer
        {
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12),
        };
        notesScroll.Content = new TextBlock
        {
            Text = notesText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
        };
        panel.Children.Add(notesScroll);

        _progress = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 100,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(_progress);

        _statusText = new TextBlock
        {
            Text = installedEdition
                ? "将下载安装器并自动运行，随后 CETUS 会退出以完成安装。"
                : "当前是便携版，无法在应用内自动安装；可以打开发布页手动下载。",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 20,
            Margin = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(_statusText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (installedEdition)
        {
            _releasesButton = CreateButton("查看发布页", OnReleasesClicked);
            buttons.Children.Add(_releasesButton);
            _installButton = CreateButton("立即更新", OnInstallClicked);
            _installButton.IsDefault = true;
            _installButton.MinWidth = 100;
            buttons.Children.Add(_installButton);
            _dismissButton = CreateButton("稍后", OnDismissClicked);
            buttons.Children.Add(_dismissButton);
        }
        else
        {
            _installButton = CreateButton("立即更新", OnInstallClicked);
            _installButton.Visibility = Visibility.Collapsed;
            _installButton.IsEnabled = false;
            _releasesButton = CreateButton("打开发布页", OnReleasesClicked);
            _releasesButton.IsDefault = true;
            _releasesButton.MinWidth = 100;
            buttons.Children.Add(_releasesButton);
            _dismissButton = CreateButton("稍后", OnDismissClicked);
            buttons.Children.Add(_dismissButton);
        }

        panel.Children.Add(buttons);
        Content = panel;
    }

    public void SetDownloading(bool downloading)
    {
        _downloading = downloading;
        _installButton.IsEnabled = !downloading;
        _releasesButton.IsEnabled = !downloading;
        _progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        _dismissButton.Content = downloading ? "取消" : "稍后";
        if (!downloading)
        {
            _progress.Value = 0;
        }
    }

    public void ReportProgress(double fraction)
    {
        _progress.Value = fraction * 100;
        _statusText.Text = $"正在下载更新… {fraction:P0}";
        _statusText.Foreground = Brushes.DimGray;
    }

    public void ReportStatus(string message, bool isError)
    {
        _statusText.Text = message;
        _statusText.Foreground = isError ? Brushes.Firebrick : Brushes.DimGray;
    }

    private void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        if (!_downloading)
        {
            InstallClicked?.Invoke();
        }
    }

    private void OnReleasesClicked(object sender, RoutedEventArgs e)
    {
        if (!_downloading)
        {
            OpenReleasesClicked?.Invoke();
        }
    }

    private void OnDismissClicked(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            CancelClicked?.Invoke();
            return;
        }

        Close();
    }

    private static Button CreateButton(string content, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 80,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
        };
        button.Click += onClick;
        return button;
    }
}
