using System.Windows;
using Cetus.Configuration;
using Cetus.Maintenance;
using Cetus.Platform;
using Cetus.Runtime;

namespace Cetus;

/// <summary>
/// Background maintenance: renderer suspension while hidden, startup
/// pruning of logs and update caches, restored-window bounds clamping
/// and the port-fallback notification.
/// </summary>
public partial class MainWindow
{
    private DateTimeOffset? _hiddenSince;
    private bool _rendererSuspended;
    private System.Windows.Threading.DispatcherTimer? _backgroundHeartbeat;
    private void OnPortFallback(object? sender, DshPortFallbackEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting)
            {
                return;
            }

            _tray?.ShowBalloonTip(
                "CETUS · 端口已切换",
                $"端口 {e.PreviousPort} 已被其他程序占用，CETUS 已自动改用 {e.NewPort} 并保存。",
                ShowWindow);
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (ParseWindowBounds(_settings.WindowBounds) is { } bounds)
        {
            double virtualLeft = SystemParameters.VirtualScreenLeft;
            double virtualTop = SystemParameters.VirtualScreenTop;
            double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            double width = Math.Min(bounds.Width, SystemParameters.VirtualScreenWidth);
            double height = Math.Min(bounds.Height, SystemParameters.VirtualScreenHeight);
            // Keep at least a grabbable strip of the window on screen.
            double left = Math.Clamp(
                bounds.Left,
                virtualRight - width - 160,
                virtualLeft + SystemParameters.VirtualScreenWidth - 160);
            double top = Math.Clamp(
                bounds.Top,
                virtualBottom - height - 120,
                virtualTop + SystemParameters.VirtualScreenHeight - 120);

            Left = left;
            Top = top;
            Width = width;
            Height = height;
            if (_settings.WindowMaximized == true)
            {
                WindowState = WindowState.Maximized;
            }
        }
    }

    /// <summary>Persists the restore-state bounds; minimized and never-shown windows keep the last values.</summary>
    private void SaveWindowPlacement()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized)
        {
            return;
        }

        bool maximized = WindowState == WindowState.Maximized;
        Rect restore = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (restore.Width <= 0 || restore.Height <= 0)
        {
            return;
        }

        _settings.SetWindowPlacement(
            $"{Math.Round(restore.Left)},{Math.Round(restore.Top)},{Math.Round(restore.Width)},{Math.Round(restore.Height)}",
            maximized);
    }

    private static (double Left, double Top, double Width, double Height)? ParseWindowBounds(string? bounds)
    {
        if (string.IsNullOrWhiteSpace(bounds))
        {
            return null;
        }

        string[] parts = bounds.Split(',');
        if (parts.Length != 4
            || !double.TryParse(parts[0], out double left)
            || !double.TryParse(parts[1], out double top)
            || !double.TryParse(parts[2], out double width)
            || !double.TryParse(parts[3], out double height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        return (left, top, width, height);
    }

    private void StartBackgroundMaintenance()
    {
        bool maintenanceDone = false;
        _backgroundHeartbeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _backgroundHeartbeat.Tick += (_, _) =>
        {
            MaintainBackgroundState();
            if (maintenanceDone || _isExiting)
            {
                return;
            }

            maintenanceDone = true;
            _ = Task.Run(() =>
            {
                try
                {
                    RetentionCleaner.PruneLogs(CetusPaths.LogDirectory);
                    RetentionCleaner.PruneStaleFiles(
                        CetusPaths.UpdateCacheDirectory, "*.exe", RetentionCleaner.DefaultUpdateCacheMaxAge);
                    RetentionCleaner.PruneStaleFiles(
                        CetusPaths.UpdateCacheDirectory, "*.zip", RetentionCleaner.DefaultUpdateCacheMaxAge);
                    RetentionCleaner.PruneStaleDirectories(
                        CetusPaths.UpdateCacheDirectory, "staging-", RetentionCleaner.DefaultUpdateCacheMaxAge);
                }
                catch (Exception error)
                {
                    Configuration.RuntimeLog.Append("background prune failed: " + error.Message);
                }
            });
        };
        _backgroundHeartbeat.Start();
    }

    private void MaintainBackgroundState()
    {
        if (_isExiting || !_startupStarted)
        {
            return;
        }

        if (IsVisible)
        {
            if (_rendererSuspended)
            {
                _browserSession.Resume();
                _rendererSuspended = false;
            }

            _hiddenSince = null;
            return;
        }

        _hiddenSince ??= DateTimeOffset.UtcNow;
        if (!_rendererSuspended && DateTimeOffset.UtcNow - _hiddenSince >= TimeSpan.FromMinutes(5))
        {
            _rendererSuspended = true;
            _ = _browserSession.TrySuspendAsync();
        }
    }
}
