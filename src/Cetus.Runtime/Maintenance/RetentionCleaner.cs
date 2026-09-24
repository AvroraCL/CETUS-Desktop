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
            List<string> candidates;
            try
            {
                candidates = Directory
                    .EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return 0;
            }

            DateTimeOffset cutoff = DateTimeOffset.UtcNow - maxAge;
            for (int index = 0; index < candidates.Count; index++)
            {
                bool beyondKeep = index >= keepNewest;
                bool tooOld;
                try
                {
                    tooOld = File.GetLastWriteTimeUtc(candidates[index]) < cutoff;
                }
                catch (Exception evalError) when (evalError is IOException or UnauthorizedAccessException)
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The directory vanished or is unreadable; nothing to prune.
            _ = error;
        }

        return deleted;
    }

    /// <summary>
    /// Removes stale staging directories (left by a failed or cancelled
    /// portable update) older than <paramref name="maxAge"/>. The current
    /// staging folder of a running update is newer than any realistic age
    /// cap, so it is never touched.
    /// </summary>
    public static int PruneStaleDirectories(string parentDirectory, string directoryPrefix, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            foreach (string candidate in Directory
                .EnumerateDirectories(parentDirectory, directoryPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(candidate) >= DateTimeOffset.UtcNow - maxAge)
                    {
                        continue;
                    }

                    Directory.Delete(candidate, recursive: true);
                    deleted++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // In use — leave it for the next pass.
                }
            }
        }
        catch (IOException)
        {
        }

        return deleted;
    }
}
