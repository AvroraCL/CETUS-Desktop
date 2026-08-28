using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cetus.Sidebar;

/// <summary>One file-browsing tab with its own root and tree state.</summary>
public partial class FilesTabContent : UserControl
{
    private string _filesRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public FilesTabContent()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFilesRoot(_filesRoot);
    }

    private void OnChooseFolderClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择文件面板的根目录",
            InitialDirectory = _filesRoot,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            LoadFilesRoot(dialog.SelectedPath);
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => LoadFilesRoot(_filesRoot);

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
        FilesTree.ItemsSource = new[] { root };
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
}
