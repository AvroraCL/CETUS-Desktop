using System.IO;
using Cetus.DshStatus;

namespace Cetus.Sidebar;

/// <summary>
/// Picks the file-panel root from the DSH session list: the session the user
/// currently selected on the DSH page wins, then the newest running session,
/// then the newest session overall. Only existing directories qualify.
/// </summary>
internal static class WorkspaceResolver
{
    public static string? ResolveRoot(IReadOnlyList<DshSessionDetail> sessions, string? currentSessionId)
    {
        if (!string.IsNullOrWhiteSpace(currentSessionId))
        {
            DshSessionDetail? selected = sessions.FirstOrDefault(
                session => string.Equals(session.SessionId, currentSessionId, StringComparison.Ordinal));
            if (ToRoot(selected) is { } selectedRoot)
            {
                return selectedRoot;
            }
        }

        DshSessionDetail? newestRunning = null;
        DshSessionDetail? newest = null;
        foreach (DshSessionDetail session in sessions)
        {
            if (ToRoot(session) is null)
            {
                continue;
            }

            if (newest is null || session.UpdatedAt > newest.UpdatedAt)
            {
                newest = session;
            }

            if (session.Running && (newestRunning is null || session.UpdatedAt > newestRunning.UpdatedAt))
            {
                newestRunning = session;
            }
        }

        return ToRoot(newestRunning) ?? ToRoot(newest);
    }

    private static string? ToRoot(DshSessionDetail? session) =>
        session is not null
        && !string.IsNullOrWhiteSpace(session.Cwd)
        && Directory.Exists(session.Cwd)
            ? session.Cwd
            : null;
}
