using Cetus.Maintenance;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class RetentionCleanerTests
{
    private static string CreateLog(string directory, string name, int ageHours)
    {
        string path = System.IO.Path.Combine(directory, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
        return path;
    }

    [Fact]
    public void PruneLogs_KeepsTheNewestFiles()
    {
        using var directory = new TemporaryDirectory();
        for (int index = 0; index < 15; index++)
        {
            CreateLog(directory.Path, $"dsh-{index:00}.log", index); // 00 newest … 14 oldest
        }

        int deleted = RetentionCleaner.PruneLogs(directory.Path, keepNewest: 10);

        Assert.Equal(5, deleted);
        Assert.Equal(10, Directory.GetFiles(directory.Path).Length);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "dsh-00.log")));
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "dsh-09.log")));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "dsh-10.log")));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "dsh-14.log")));
    }

    [Fact]
    public void PruneLogs_DeletesFilesBeyondTheAgeCap()
    {
        using var directory = new TemporaryDirectory();
        CreateLog(directory.Path, "ancient.log", ageHours: 24 * 60); // 60 days old, but the only one
        CreateLog(directory.Path, "fresh.log", ageHours: 1);

        int deleted = RetentionCleaner.PruneLogs(directory.Path, keepNewest: 10, maxAgeDays: 30);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "fresh.log")));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "ancient.log")));
    }

    [Fact]
    public void PruneStaleFiles_SurvivesLockedFiles()
    {
        using var directory = new TemporaryDirectory();
        string locked = CreateLog(directory.Path, "locked.log", ageHours: 24 * 31);
        CreateLog(directory.Path, "stale.log", ageHours: 24 * 31);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            int deleted = RetentionCleaner.PruneStaleFiles(directory.Path, "*.log", TimeSpan.FromDays(30));

            // The locked file cannot go, the unlocked one must not be skipped over.
            Assert.Equal(1, deleted);
            Assert.True(File.Exists(locked));
        }
    }

    [Fact]
    public void PruneStaleFiles_MissingDirectory_YieldsZero() =>
        Assert.Equal(0, RetentionCleaner.PruneStaleFiles(
            System.IO.Path.Combine(Path.GetTempPath(), "cetus-no-such-dir"), "*.log", TimeSpan.FromDays(1)));

    [Fact]
    public void PruneStaleDirectories_RemovesOnlyOldPrefixedDirectories()
    {
        using var directory = new TemporaryDirectory();
        string oldStaging = System.IO.Path.Combine(directory.Path, "staging-0.1.0");
        string freshStaging = System.IO.Path.Combine(directory.Path, "staging-0.2.0");
        string unprefixed = System.IO.Path.Combine(directory.Path, "keep-me");
        Directory.CreateDirectory(oldStaging);
        Directory.CreateDirectory(freshStaging);
        Directory.CreateDirectory(unprefixed);
        File.WriteAllText(System.IO.Path.Combine(oldStaging, "f.txt"), "x");
        Directory.SetLastWriteTimeUtc(oldStaging, DateTime.UtcNow.AddDays(-8));

        int deleted = RetentionCleaner.PruneStaleDirectories(
            directory.Path, "staging-", TimeSpan.FromDays(7));

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(oldStaging));
        Assert.True(Directory.Exists(freshStaging));
        Assert.True(Directory.Exists(unprefixed));
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
