using System.Drawing;
using System.Windows.Forms;
using Cetus.Configuration;

namespace Cetus.Platform;

internal sealed record TrayCommands(
    Action ShowWindow,
    Func<Task> RetryDsh,
    Func<Task> ConfigurePort,
    Func<Task> CheckForUpdates,
    Action ExitApplication,
    Action<string> OpenWorkspace,
    Func<string?> PickWorkspace);

/// <summary>
/// Owns the notification-area icon, menu and Explorer restart recovery.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _retryItem;
    private readonly ToolStripMenuItem _recentItem;
    private readonly ToolStripMenuItem _openWorkspaceItem;
    private readonly Func<string?> _pickWorkspace;
    private readonly Action<string> _openWorkspace;
    private Action? _balloonClick;
    private bool _disposed;

    public TrayIconController(TrayCommands commands)
    {
        _icon = ResolveIcon();
        _openWorkspace = commands.OpenWorkspace;
        _pickWorkspace = commands.PickWorkspace;
        _menu = new ContextMenuStrip();
        _menu.Items.Add("显示窗口", null, (_, _) => commands.ShowWindow());

        _recentItem = new ToolStripMenuItem("最近工作区");
        _recentItem.DropDownItems.Add(new ToolStripMenuItem("暂无记录") { Enabled = false });
        _menu.Items.Add(_recentItem);

        _openWorkspaceItem = new ToolStripMenuItem("打开工作区…");
        _openWorkspaceItem.Click += (_, _) =>
        {
            if (_pickWorkspace.Invoke() is { } workspace)
            {
                _openWorkspace.Invoke(workspace);
            }
        };
        _menu.Items.Add(_openWorkspaceItem);

        _retryItem = new ToolStripMenuItem("重试连接 DSH");
        _retryItem.Click += async (_, _) => await commands.RetryDsh();
        _menu.Items.Add(_retryItem);

        var configurePortItem = new ToolStripMenuItem("设置 DSH 端口…");
        configurePortItem.Click += async (_, _) => await commands.ConfigurePort();
        _menu.Items.Add(configurePortItem);

        var checkUpdatesItem = new ToolStripMenuItem("检查更新…");
        checkUpdatesItem.Click += async (_, _) => await commands.CheckForUpdates();
        _menu.Items.Add(checkUpdatesItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => commands.ExitApplication());

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "CETUS鲸鱼座",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => commands.ShowWindow();
        _tray.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    /// <summary>
    /// Shows a notification-area balloon; the optional click action replaces
    /// whatever a previous balloon registered (balloons stack, the click
    /// target must always match the newest message).
    /// </summary>
    public void ShowBalloonTip(string title, string message, Action? onClick = null)
    {
        if (_disposed)
        {
            return;
        }

        _balloonClick = onClick;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = message;
        _tray.ShowBalloonTip(8000);
    }

    public void SetRetryEnabled(bool enabled)
    {
        if (!_disposed)
        {
            _retryItem.Enabled = enabled;
        }
    }

    /// <summary>Rebuilds the recent-workspaces submenu from the newest-first list.</summary>
    public void SetRecentWorkspaces(IReadOnlyList<RecentWorkspace> entries)
    {
        if (_disposed)
        {
            return;
        }

        _recentItem.DropDownItems.Clear();
        if (entries.Count == 0)
        {
            _recentItem.DropDownItems.Add(new ToolStripMenuItem("暂无记录") { Enabled = false });
            return;
        }

        foreach (RecentWorkspace entry in entries)
        {
            ToolStripMenuItem item = new(entry.Title)
            {
                ToolTipText = entry.Path,
            };
            item.Click += (_, _) => _openWorkspace.Invoke(entry.Path);
            _recentItem.DropDownItems.Add(item);
        }
    }

    public void RestoreAfterExplorerRestart()
    {
        if (_disposed)
        {
            return;
        }

        _tray.Visible = false;
        _tray.Visible = true;
    }

    private static Icon ResolveIcon()
    {
        string? executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
            catch
            {
                // Fall back to the standard application icon.
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
