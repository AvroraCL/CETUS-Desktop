using Cetus.Configuration;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class RecentWorkspacesTests
{
    [Fact]
    public void Add_NormalizesSeparatorsAndDerivesTitle()
    {
        using var directory = new TemporaryDirectory();
        string filePath = System.IO.Path.Combine(directory.Path, "recent.json");
        var workspaces = new RecentWorkspaces(filePath);

        RecentWorkspace entry = workspaces.Add(@"F:\repos\demo\");

        Assert.Equal(@"F:\repos\demo", entry.Path);
        Assert.Equal("demo", entry.Title);
    }

    [Fact]
    public void Add_DeduplicatesByPathAndMovesToFront()
    {
        var entries = new List<RecentWorkspace>
        {
            new(@"F:\a", "a", DateTimeOffset.UtcNow),
            new(@"F:\b", "b", DateTimeOffset.UtcNow),
        };

        List<RecentWorkspace> promoted = RecentWorkspaces.Promote(
            entries,
            new RecentWorkspace(@"F:\A", "A", DateTimeOffset.UtcNow),
            maxEntries: RecentWorkspaces.MaxEntries);

        Assert.Equal(@"F:\A", promoted[0].Path);
        Assert.Equal(2, promoted.Count);
        Assert.Equal(@"F:\b", promoted[1].Path);
    }

    [Fact]
    public void Promote_CapsAtMaxEntries()
    {
        var entries = Enumerable.Range(0, RecentWorkspaces.MaxEntries + 5)
            .Select(index => new RecentWorkspace($@"F:\w{index}", $"w{index}", DateTimeOffset.UtcNow))
            .ToList();

        List<RecentWorkspace> promoted = RecentWorkspaces.Promote(
            entries,
            new RecentWorkspace(@"F:\new", "new", DateTimeOffset.UtcNow),
            maxEntries: RecentWorkspaces.MaxEntries);

        Assert.Equal(RecentWorkspaces.MaxEntries, promoted.Count);
        Assert.Equal(@"F:\new", promoted[0].Path);
        Assert.Equal(@"F:\w0", promoted[1].Path);
    }

    [Fact]
    public void Add_PersistsAcrossReloads()
    {
        using var directory = new TemporaryDirectory();
        string filePath = System.IO.Path.Combine(directory.Path, "nested", "recent.json");
        var first = new RecentWorkspaces(filePath);
        first.Add(@"F:\repos\demo");

        var second = new RecentWorkspaces(filePath);

        RecentWorkspace entry = Assert.Single(second.Entries);
        Assert.Equal(@"F:\repos\demo", entry.Path);
        Assert.Equal("demo", entry.Title);
        Assert.True(entry.LastUsed > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Load_CorruptFile_YieldsEmptyListAndRecovers()
    {
        using var directory = new TemporaryDirectory();
        string filePath = System.IO.Path.Combine(directory.Path, "recent.json");
        File.WriteAllText(filePath, "{ not json");

        var workspaces = new RecentWorkspaces(filePath);
        Assert.Empty(workspaces.Entries);

        workspaces.Add(@"F:\repos\demo");
        var reloaded = new RecentWorkspaces(filePath);
        Assert.Single(reloaded.Entries);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestWorkspace.CreateDirectory();
        }

        public string Path { get; }

        public void Dispose()
        {
            if (TestWorkspace.RetainArtifacts) return;
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Leave failed-test artifacts for diagnosis.
            }
        }
    }
}
