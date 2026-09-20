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
                if (File.Exists(CurrentLogFile) && IsFull(CurrentLogFile))
                {
                    Rotate(CurrentLogFile);
                }

                File.AppendAllText(
                    CurrentLogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsFull(string path) =>
        new FileInfo(path).Length > MaxLinesPerFile * 90;

    private static void Rotate(string path)
    {
        string rotated = path.Replace(".log", ".old.log");
        File.Delete(rotated);
        File.Move(path, rotated);
    }
}
