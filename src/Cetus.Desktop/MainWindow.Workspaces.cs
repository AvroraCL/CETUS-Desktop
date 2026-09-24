using System.IO;
using System.Net.Http;
using System.Windows;
using Cetus.Configuration;
using Cetus.DshStatus;
using Cetus.Platform;
using Cetus.Runtime;

namespace Cetus;

/// <summary>
/// Workspace activation: launch args, IPC forwarding, Jump List and tray
/// entries all land here; a pending path waits for the first page load.
/// </summary>
public partial class MainWindow
{
    private readonly Queue<string> _pendingWorkspacePaths = new();
    private bool _isOpeningWorkspace;
    /// <summary>
    /// Entry point for workspace activations (launch args, IPC forwarding,
    /// Jump List, tray). A null path only summons the window; before the
    /// runtime is ready the path is parked until the first page load.
    /// </summary>
    public void ActivateWorkspace(string? path)
    {
        if (_isExiting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            ShowWindow();
            return;
        }

        if (_runtime.State.Phase != DesktopRuntimePhase.Ready || !_browserSession.IsInitialized || _isOpeningWorkspace)
        {
            // Queue instead of overwriting: every forwarded workspace is opened
            // in order once the runtime is ready (or the current open finishes).
            _pendingWorkspacePaths.Enqueue(path);
            return;
        }

        _ = OpenWorkspaceAsync(path);
    }

    private string? ConsumePendingWorkspace()
    {
        return _pendingWorkspacePaths.Count > 0 ? _pendingWorkspacePaths.Dequeue() : null;
    }

    private async Task OpenWorkspaceAsync(string path)
    {
        string normalized = RecentWorkspaces.NormalizePath(path);
        if (!Directory.Exists(normalized))
        {
            // Summon the window too: a stale Jump List / shortcut forwarded by
            // a second launch must not leave the user staring at nothing.
            ShowWindow();
            _tray?.ShowBalloonTip("CETUS · 工作区", $"目录不存在：{normalized}", ShowWindow);
            return;
        }

        if (_isOpeningWorkspace)
        {
            _pendingWorkspacePaths.Enqueue(normalized);
            return;
        }

        _isOpeningWorkspace = true;
        try
        {
            ShowWindow();
            _recentWorkspaces.Add(normalized);
            RefreshWorkspaceEntries();

            Uri endpoint = _runtime.Endpoint;
            _dshSessionClient ??= new DshSessionClient(_settings.DshHomeOverride);
            string workspaceId = await _dshSessionClient.CreateWorkspaceAsync(
                endpoint, normalized, CancellationToken.None);
            string sessionId = await _dshSessionClient.CreateSessionAsync(
                endpoint, workspaceId, CancellationToken.None);
            await FocusSessionCoreAsync(sessionId);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (!_isExiting)
            {
                _tray?.ShowBalloonTip("CETUS · 无法打开工作区", $"{normalized}\n{error.Message}");
            }
        }
        finally
        {
            _isOpeningWorkspace = false;
            if (ConsumePendingWorkspace() is { } next)
            {
                _ = OpenWorkspaceAsync(next);
            }
        }
    }

    /// <summary>Summon the window and make the DSH UI focus a specific session on reload.</summary>
    private async Task FocusSessionAsync(string sessionId)
    {
        ShowWindow();
        try
        {
            await FocusSessionCoreAsync(sessionId);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The window is already up; a failed refocus must not surface.
        }
    }

    private async Task FocusSessionCoreAsync(string sessionId)
    {
        await _browserSession.ExecuteScriptAsync(
            $"localStorage.setItem('dsh.sessions.current', JSON.stringify({_sessionSelectionScriptValue(sessionId)}))");
        await _runtime.NavigateHomeAsync();
    }

    private static string _sessionSelectionScriptValue(string sessionId) =>
        System.Text.Json.JsonSerializer.Serialize(new { sessionId });

    private void RefreshWorkspaceEntries()
    {
        IReadOnlyList<RecentWorkspace> entries = _recentWorkspaces.Entries;
        _tray?.SetRecentWorkspaces(entries);
        JumpListController.Apply(entries);
    }
}
