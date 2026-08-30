using Cetus.DshStatus;
using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class WorkspaceResolverTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 12, 0, 0);

    private static string ExistingDirectory()
    {
        string root = TestWorkspace.CreateDirectory();
        return root;
    }

    private static DshSessionDetail Session(
        string id,
        string cwd,
        bool running = false,
        int ageMinutes = 0) =>
        new(id, "session", cwd, running, BaseTime.AddMinutes(-ageMinutes),
            Usage: null, Turns: 0, Steps: 0, LlmMilliseconds: 0, ToolMilliseconds: 0,
            Pressure: null, Todos: Array.Empty<DshTodo>());

    [Fact]
    public void ResolveRoot_PrefersSelectedSessionOverRunning()
    {
        string root = ExistingDirectory();
        try
        {
            var sessions = new[]
            {
                Session("running", Environment.SystemDirectory, running: true, ageMinutes: 1),
                Session("selected", root, ageMinutes: 5),
            };

            string? resolved = WorkspaceResolver.ResolveRoot(sessions, "selected");

            Assert.Equal(root, resolved);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveRoot_FallsBackToNewestRunningWhenSelectionUnknown()
    {
        string oldDir = ExistingDirectory();
        string newDir = ExistingDirectory();
        try
        {
            var sessions = new[]
            {
                Session("old-running", oldDir, running: true, ageMinutes: 10),
                Session("new-running", newDir, running: true, ageMinutes: 2),
            };

            string? resolved = WorkspaceResolver.ResolveRoot(sessions, "missing-id");

            Assert.Equal(newDir, resolved);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(oldDir, recursive: true);
                Directory.Delete(newDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveRoot_FallsBackToNewestSessionWhenNothingRuns()
    {
        string oldDir = ExistingDirectory();
        string newDir = ExistingDirectory();
        try
        {
            var sessions = new[]
            {
                Session("old", oldDir, ageMinutes: 30),
                Session("new", newDir, ageMinutes: 1),
            };

            string? resolved = WorkspaceResolver.ResolveRoot(sessions, currentSessionId: null);

            Assert.Equal(newDir, resolved);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(oldDir, recursive: true);
                Directory.Delete(newDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveRoot_SkipsSessionsWithMissingDirectories()
    {
        string root = ExistingDirectory();
        try
        {
            var sessions = new[]
            {
                Session("gone", Path.Combine(root, "does-not-exist"), running: true, ageMinutes: 1),
                Session("real", root, ageMinutes: 9),
            };

            string? resolved = WorkspaceResolver.ResolveRoot(sessions, "gone");

            Assert.Equal(root, resolved);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveRoot_ReturnsNullWithoutValidDirectories()
    {
        var sessions = new[]
        {
            Session("a", "Z:\\definitely\\missing"),
            Session("b", string.Empty),
        };

        Assert.Null(WorkspaceResolver.ResolveRoot(sessions, "a"));
        Assert.Null(WorkspaceResolver.ResolveRoot(Array.Empty<DshSessionDetail>(), null));
    }
}
