using System.Drawing;
using System.Windows.Forms;

namespace Cetus.Platform;

internal sealed record TrayCommands(
    Action ShowWindow,
    Func<Task> RetryDsh,
    Func<Task> ConfigurePort,
    Action ExitApplication);

/// <summary>
/// Owns the notification-area icon, menu and Explorer restart recovery.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _retryItem;
    private bool _disposed;

    public TrayIconController(TrayCommands commands)
    {
        _icon = ResolveIcon();
        _menu = new ContextMenuStrip();
        _menu.Items.Add("显示窗口", null, (_, _) => commands.ShowWindow());

        _retryItem = new ToolStripMenuItem("重试连接 DSH");
        _retryItem.Click += async (_, _) => await commands.RetryDsh();
        _menu.Items.Add(_retryItem);

        var configurePortItem = new ToolStripMenuItem("设置 DSH 端口…");
        configurePortItem.Click += async (_, _) => await commands.ConfigurePort();
        _menu.Items.Add(configurePortItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => commands.ExitApplication());

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Cetus · 鲸鱼座",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => commands.ShowWindow();
    }

    public void SetRetryEnabled(bool enabled)
    {
        if (!_disposed)
        {
            _retryItem.Enabled = enabled;
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
