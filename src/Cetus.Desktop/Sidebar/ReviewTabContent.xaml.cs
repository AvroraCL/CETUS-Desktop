using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Cetus.Configuration;
using Cetus.DshStatus;

namespace Cetus.Sidebar;

/// <summary>
/// Code change reviewer: lists the files a DSH session changed in its
/// workspace (git status) and shows a per-file diff against HEAD.
/// </summary>
public partial class ReviewTabContent : UserControl, IDisposable
{
    private const int MaxDiffLines = 4000;

    private readonly DshStatusClient _statusClient = new();
    private Func<Uri>? _endpointProvider;
    private List<(string Cwd, string Label, bool Running)> _workspaces = new();
    private int _selectedWorkspace;
    private int _refreshGeneration;
    private bool _loaded;
    private string? _repositoryRoot;

    public ReviewTabContent()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadAsync();
    }

    /// <summary>Provides the live DSH endpoint (port changes follow).</summary>
    public void SetEndpointProvider(Func<Uri> provider) => _endpointProvider = provider;

    public void Dispose()
    {
        ++_refreshGeneration;
        _endpointProvider = null;
        _repositoryRoot = null;
        _statusClient.Dispose();
    }

    private async void LoadAsync()
    {
        if (_loaded)
        {
            await RefreshAsync();
            return;
        }

        _loaded = true;
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnSessionClicked(object sender, RoutedEventArgs e)
    {
        if (_workspaces.Count > 0)
        {
            _selectedWorkspace = (_selectedWorkspace + 1) % _workspaces.Count;
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (_endpointProvider is null)
        {
            return;
        }

        int generation = ++_refreshGeneration;
        _repositoryRoot = null;
        FilesList.ItemsSource = null;
        try
        {
            Uri endpoint = _endpointProvider();
            DshStatusSnapshot snapshot = await _statusClient.GetStatusAsync(
                endpoint,
                CancellationToken.None);
            if (generation != _refreshGeneration)
            {
                return;
            }

            _workspaces = snapshot.Sessions
                .Where(detail => !string.IsNullOrWhiteSpace(detail.Cwd) && Directory.Exists(detail.Cwd))
                .GroupBy(detail => detail.Cwd, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(detail => detail.Running)
                    .ThenByDescending(detail => detail.UpdatedAt).First())
                .Select(detail => (detail.Cwd, Label: Path.GetFileName(
                    detail.Cwd.TrimEnd(Path.DirectorySeparatorChar)), detail.Running))
                .OrderByDescending(item => item.Running)
                .ToList();
            if (_workspaces.Count == 0)
            {
                SetDiff(new[] { DiffLine.Info("没有可审查的会话工作目录：先在 DSH 中打开一个工作区会话。") });
                FilesList.ItemsSource = null;
                return;
            }

            if (_selectedWorkspace >= _workspaces.Count)
            {
                _selectedWorkspace = 0;
            }

            (string cwd, string label, bool running) = _workspaces[_selectedWorkspace];
            SessionButton.Content = label + (running ? " · 运行中" : string.Empty);
            await LoadChangesAsync(generation, cwd);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (generation == _refreshGeneration)
            {
                SetDiff(new[] { DiffLine.Info($"无法连接 DSH：{error.Message}") });
                FilesList.ItemsSource = null;
            }
        }
    }

    private async Task LoadChangesAsync(int generation, string cwd)
    {
        try
        {
            (string root, IReadOnlyList<ReviewFile> files) = await ReviewService.GetChangesAsync(cwd);
            if (generation != _refreshGeneration)
            {
                return;
            }

            _repositoryRoot = root;
            if (files.Count == 0)
            {
                FilesList.ItemsSource = null;
                SetDiff(new[] { DiffLine.Info("工作区是干净的，没有待审查的改动 ✓") });
                return;
            }

            FilesList.ItemsSource = files;
            FilesList.SelectedIndex = 0;
        }
        catch (Win32Exception error)
        {
            if (generation != _refreshGeneration)
            {
                return;
            }

            SetDiff(new[] { DiffLine.Info($"读取改动失败：{error.Message}") });
            FilesList.ItemsSource = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (generation == _refreshGeneration)
            {
                SetDiff(new[] { DiffLine.Info($"读取改动失败：{error.Message}") });
            }
        }
    }

    private async void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FilesList.SelectedItem is not ReviewFile file
            || _repositoryRoot is null)
        {
            return;
        }

        int generation = ++_refreshGeneration;
        string cwd = _repositoryRoot;
        try
        {
            if (file.Status == "??")
            {
                IReadOnlyList<DiffLine> untracked = await ReviewService.ReadUntrackedAsync(cwd, file.Path);
                if (generation != _refreshGeneration)
                {
                    return;
                }

                SetDiff(untracked);
                return;
            }

            (int exitCode, string output) = await GitRunner.RunAsync(
                cwd, new[] { "diff", "HEAD", "--", file.Path });
            if (generation != _refreshGeneration)
            {
                return;
            }

            SetDiff(exitCode == 0 ? ParseDiff(output) : new[] { DiffLine.Info("git diff 执行失败，请确认仓库已有提交且文件可读。") });
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (generation != _refreshGeneration)
            {
                return;
            }

            SetDiff(new[] { DiffLine.Info($"读取改动失败：{error.Message}") });
        }
    }

    private static IReadOnlyList<DiffLine> ParseDiff(string output)
    {
        var lines = new List<DiffLine>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff --git")
                || line.StartsWith("index ", StringComparison.Ordinal) || line.StartsWith("new file")
                || line.StartsWith("deleted file") || line.StartsWith("old mode")
                || line.StartsWith("new mode") || line.StartsWith("similarity index"))
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine("hunk", line));
            }
            else if (line.StartsWith('+'))
            {
                lines.Add(new DiffLine("add", line));
            }
            else if (line.StartsWith('-'))
            {
                lines.Add(new DiffLine("del", line));
            }
            else if (line.StartsWith("Binary files", StringComparison.Ordinal))
            {
                lines.Add(DiffLine.Info("二进制文件，无法显示 diff。"));
            }
            else
            {
                lines.Add(new DiffLine("ctx", line));
            }

            if (lines.Count >= MaxDiffLines)
            {
                lines.Add(DiffLine.Info($"… diff 过大，已截断（前 {MaxDiffLines} 行）"));
                break;
            }
        }

        return lines.Count == 0
            ? new[] { DiffLine.Info("该文件在暂存后没有未提交的差异。") }
            : lines;
    }

    private void SetDiff(IReadOnlyList<DiffLine> lines)
    {
        DiffList.ItemsSource = lines;
        if (DiffList.Items.Count > 0)
        {
            DiffList.ScrollIntoView(DiffList.Items[0]);
        }
    }
}
