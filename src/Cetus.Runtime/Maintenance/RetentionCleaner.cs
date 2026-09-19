using System.IO;

namespace Cetus.Maintenance;

/// <summary>
/// Disk hygiene for long-lived tray installs: sidecar logs accumulate one
/// file per DSH launch and downloaded installers outlive the update that
/// used them. Pruning is best effort — a locked or unreadable file is
/// skipped, never fatal.
/// </summary>
public static class RetentionCleaner
{
    public const int DefaultLogKeep = 10;
    public const int DefaultLogMaxAgeDays = 30;
    public static readonly TimeSpan DefaultUpdateCacheMaxAge = TimeSpan.FromDays(7);

    /// <summary>Deletes the oldest *.log files beyond <paramref name="keepNewest"/> or older than the age cap.</summary>
    public static int PruneLogs(string logDirectory, int keepNewest = DefaultLogKeep, int maxAgeDays = DefaultLogMaxAgeDays) =>
        PruneStaleFiles(logDirectory, "*.log", TimeSpan.FromDays(maxAgeDays), keepNewest);

    /// <summary>Deletes matching files older than <paramref name="maxAge"/>.</summary>
    public static int PruneStaleFiles(string directory, string searchPattern, TimeSpan maxAge, int keepNewest = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            string[] candidates = Directory
                .EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - maxAge;
            for (int index = 0; index < candidates.Length; index++)
            {
                bool beyondKeep = index >= keepNewest;
                bool tooOld;
                try
                {
                    tooOld = File.GetLastWriteTimeUtc(candidates[index]) < cutoff;
                }
                catch (IOException)
                {
                    continue;
                }

                if (!beyondKeep && !tooOld)
                {
                    continue;
                }

                try
                {
                    File.Delete(candidates[index]);
                    deleted++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // In use (current sidecar log) or ACL-locked — leave it.
                }
            }
        }
        catch (IOException)
        {
            // The directory vanished or is unreadable; nothing to prune.
        }

        return deleted;
    }
}
