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
            (int toplevelExit, string _) = await GitRunner.RunAsync(
                cwd, new[] { "rev-parse", "--is-inside-work-tree" });
            if (toplevelExit != 0)
            {
                SetDiff(new[] { DiffLine.Info("当前会话目录不是 git 仓库，无法审查改动。") });
                FilesList.ItemsSource = null;
                return;
            }

            (int statusExit, string statusOutput) = await GitRunner.RunAsync(
                cwd, new[] { "status", "--porcelain=v1" });
            if (generation != _refreshGeneration)
            {
                return;
            }

            if (statusExit != 0)
            {
                SetDiff(new[] { DiffLine.Info("git status 执行失败，请确认该目录可读。") });
                FilesList.ItemsSource = null;
                return;
            }

            List<ReviewFile> files = statusOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 3)
                .Select(line =>
                {
                    string status = line[..2].Trim();
                    string path = line[3..];
                    int renameMarker = path.IndexOf(" -> ", StringComparison.Ordinal);
                    if (renameMarker >= 0)
                    {
                        path = path[(renameMarker + 4)..];
                    }

                    path = path.Trim('"');
                    return new ReviewFile(status.Length == 0 ? "M" : status, path);
                })
                .ToList();

            if (files.Count == 0)
            {
                FilesList.ItemsSource = null;
                SetDiff(new[] { DiffLine.Info("工作区是干净的，没有待审查的改动 ✓") });
                return;
            }

            FilesList.ItemsSource = files;
            FilesList.SelectedIndex = 0;
        }
        catch (Win32Exception)
        {
            SetDiff(new[] { DiffLine.Info("未找到 git，请安装 Git 后重试。") });
            FilesList.ItemsSource = null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
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
            || _selectedWorkspace >= _workspaces.Count)
        {
            return;
        }

        int generation = ++_refreshGeneration;
        string cwd = _workspaces[_selectedWorkspace].Cwd;
        try
        {
            if (file.Status == "??")
            {
                SetDiff(await UntrackedDiffAsync(file.Path));
                return;
            }

            (int exitCode, string output) = await GitRunner.RunAsync(
                cwd, new[] { "diff", "HEAD", "--", file.Path });
            if (generation != _refreshGeneration)
            {
                return;
            }

            SetDiff(ParseDiff(output));
        }
        catch (Win32Exception)
        {
            SetDiff(new[] { DiffLine.Info("未找到 git，请安装 Git 后重试。") });
        }
    }

    private static async Task<IReadOnlyList<DiffLine>> UntrackedDiffAsync(string relativePath)
    {
        string fullPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (!File.Exists(fullPath))
        {
            return new[] { DiffLine.Info("未跟踪文件已不存在。") };
        }

        var lines = new List<DiffLine>
        {
            new("hunk", $"@@ 未跟踪文件（全部视为新增）: {relativePath} @@"),
        };
        foreach (string line in await File.ReadAllLinesAsync(fullPath))
        {
            lines.Add(new DiffLine("add", "+" + line));
            if (lines.Count >= 800)
            {
                lines.Add(DiffLine.Info("… 文件过大，仅显示前 800 行"));
                break;
            }
        }

        return lines;
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
