using System.IO;

namespace Cetus.Configuration;

/// <summary>
/// Lightweight file log for runtime lifecycle events (spawn, readiness,
/// failures, recovery, port fallback) so reports like "DSH keeps
/// restarting" can be diagnosed from the logs directory alone. Best
/// effort: logging never throws and never blocks startup beyond a write.
/// </summary>
public static class RuntimeLog
{
    private const int MaxLinesPerFile = 4000;
    private static readonly object Gate = new();

    public static string CurrentLogFile => Path.Combine(
        CetusPaths.LogDirectory,
        $"cetus-{DateTime.UtcNow:yyyyMMdd}.log");

    public static void Append(string message)
    {
        try
        {
            string? directory = Path.GetDirectoryName(CurrentLogFile);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            lock (Gate)
            {
                if (File.Exists(CurrentLogFile) && IsFull(CurrentLogFile) && TryRotate(CurrentLogFile))
                {
                    // rotated to .old.log; the append below starts a fresh file
                }

                // File.AppendAllText opens with FileShare.Read only, so a
                // second concurrent Cetus process would silently lose every
                // line. Share ReadWrite like the log readers do.
                using (var stream = new FileStream(
                    CurrentLogFile,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Rotation threshold in bytes (~360 KB, about MaxLinesPerFile short lines).
    private const long MaxLogBytes = MaxLinesPerFile * 90L;

    private static bool IsFull(string path) =>
        new FileInfo(path).Length > MaxLogBytes;

    /// <summary>
    /// Best-effort rotation: when .old.log is held open by a reader (e.g. the
    /// diagnostics export), keep appending to the current file instead of
    /// dropping the entry.
    /// </summary>
    private static bool TryRotate(string path)
    {
        try
        {
            string rotated = path.Replace(".log", ".old.log");
            File.Delete(rotated);
            File.Move(path, rotated);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
