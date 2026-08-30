using Cetus.DshStatus;

namespace Cetus.Sidebar;

/// <summary>
/// Tracks the session the user currently selected on the DSH page (bridged
/// from the WebView's persisted selection) and resolves it to a filesystem
/// root for the workspace-following file panel.
/// </summary>
internal sealed class WorkspaceTracker : IDisposable
{
    private readonly DshStatusClient _statusClient = new();
    private readonly object _gate = new();
    private string? _currentSessionId;

    /// <summary>Raised when the DSH selection changes (on the WebView thread).</summary>
    public event Action? SelectionChanged;

    public void UpdateSelection(string? sessionId)
    {
        lock (_gate)
        {
            if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            _currentSessionId = sessionId;
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>Resolves the file root for the tracked selection via session.list.</summary>
    public async Task<string?> ResolveRootAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        string? sessionId;
        lock (_gate)
        {
            sessionId = _currentSessionId;
        }

        IReadOnlyList<DshSessionDetail> sessions =
            await _statusClient.GetSessionsAsync(endpoint, cancellationToken);
        return WorkspaceResolver.ResolveRoot(sessions, sessionId);
    }

    public void Dispose() => _statusClient.Dispose();
}
