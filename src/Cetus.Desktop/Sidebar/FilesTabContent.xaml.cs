using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cetus.Sidebar;

/// <summary>
/// Workspace file browser: the tree root follows the DSH session the user
/// currently has open, and selecting a file shows an inline text/image
/// preview; double-click still opens the system-associated application.
/// </summary>
public sealed partial class FilesTabContent : UserControl, IDisposable
{
    private string _filesRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private bool _initialized;
    private int _previewGeneration;
    private CancellationTokenSource? _previewCts;
    private bool _disposed;

    public FilesTabContent()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // Keep the preview image inside the pane; Stretch=Uniform then scales
        // it up or down to fit while preserving the aspect ratio.
        PreviewImageHost.SizeChanged += (_, e) =>
        {
            PreviewImage.MaxWidth = Math.Max(0, e.NewSize.Width - 16);
            PreviewImage.MaxHeight = Math.Max(0, e.NewSize.Height - 16);
        };
    }

    /// <summary>Resolves the DSH workspace root; null keeps the current root.</summary>
    public Func<CancellationToken, Task<string?>>? WorkspaceResolver { get; set; }

    public void Dispose()
    {
        _disposed = true;
        _previewGeneration++;
        _previewCts?.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await FollowWorkspaceAsync();
    }

    /// <summary>Re-resolves the DSH workspace and follows the new root.</summary>
    public async Task RefreshWorkspaceAsync()
    {
        if (_disposed)
        {
            return;
        }

        await FollowWorkspaceAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await FollowWorkspaceAsync();
        if (!_disposed)
        {
            LoadFilesRoot(_filesRoot);
        }
    }

    private async Task FollowWorkspaceAsync()
    {
        string? root = null;
        if (WorkspaceResolver is not null)
        {
            try
            {
                root = await WorkspaceResolver(CancellationToken.None);
            }
            catch (Exception)
            {
                // DSH unreachable: keep showing the last valid root.
                root = null;
            }
        }

        if (_disposed)
        {
            return;
        }

        if (root is not null && IsRenderableDirectory(root) && !SamePath(root, _filesRoot))
        {
            LoadFilesRoot(root);
        }
        else if (FilesTree.ItemsSource is null)
        {
            LoadFilesRoot(_filesRoot);
        }
    }

    private void LoadFilesRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        _filesRoot = path;
        PathBox.Text = path;
        var root = new SidebarFileNode(path, isDirectory: true);
        root.LoadChildren();
        root.IsExpanded = true;
        FilesTree.ItemsSource = new[] { root };
        ClearPreview();
    }

    private void OnFileNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: SidebarFileNode node })
        {
            node.LoadChildren();
        }
    }

    private void OnFileNodeDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FilesTree, e.OriginalSource as DependencyObject)
            is not TreeViewItem { DataContext: SidebarFileNode { IsDirectory: false } node })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _ = MessageBox.Show(
                Window.GetWindow(this),
                $"无法打开文件：{error.Message}",
                "CETUS · 文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnFileSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SidebarFileNode { IsDirectory: false, IsPlaceholder: false } node)
        {
            _ = LoadPreviewAsync(node.FullPath);
        }
    }

    private async Task LoadPreviewAsync(string path)
    {
        int generation = ++_previewGeneration;
        CancellationTokenSource? previous = _previewCts;
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        previous?.Cancel();
        try
        {
            FilePreviewResult result = await FilePreviewService.LoadAsync(path, cts.Token);
            if (_disposed || generation != _previewGeneration)
            {
                return;
            }

            ShowPreview(path, result);
        }
        catch (OperationCanceledException)
        {
            // A newer selection took over.
        }
        catch (Exception error)
        {
            if (!_disposed && generation == _previewGeneration)
            {
                ShowNotice($"预览失败：{error.Message}");
            }
        }
    }

    private void ShowPreview(string path, FilePreviewResult result)
    {
        PreviewHeader.Text = $"{Path.GetFileName(path)} · {FormatSize(result.FileSize)}"
            + (result.IsTruncated ? " · 已截断" : string.Empty);
        PreviewHeader.ToolTip = path;
        PreviewLines.Visibility = result.Content == FilePreviewContent.Text
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewImageHost.Visibility = result.Content == FilePreviewContent.Image
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewNoticePanel.Visibility = result.Content == FilePreviewContent.Notice
            ? Visibility.Visible
            : Visibility.Collapsed;
        switch (result.Content)
        {
            case FilePreviewContent.Text:
                PreviewLines.ItemsSource = result.Lines;
                if (PreviewLines.Items.Count > 0)
                {
                    PreviewLines.ScrollIntoView(PreviewLines.Items[0]);
                }

                break;
            case FilePreviewContent.Image:
                PreviewImage.Source = result.Image;
                break;
            case FilePreviewContent.Notice:
                PreviewNoticeText.Text = result.Notice;
                break;
        }
    }

    private void ShowNotice(string text)
    {
        PreviewHeader.Text = string.Empty;
        PreviewLines.Visibility = Visibility.Collapsed;
        PreviewImageHost.Visibility = Visibility.Collapsed;
        PreviewNoticePanel.Visibility = Visibility.Visible;
        PreviewNoticeText.Text = text;
        PreviewImage.Source = null;
    }

    private void ClearPreview() => ShowNotice("选择文件以预览内容。");

    private static bool IsRenderableDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    private static bool SamePath(string? left, string right) =>
        string.Equals(
            left?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes) =>
        bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
            >= 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes} B",
        };
}
