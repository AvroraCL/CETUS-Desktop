using System.IO;

namespace Cetus.Desktop.Core;

/// <summary>
/// Tiny file-based trace for the shell itself (sidecar logs live in dsh-*.log).
/// Useful during M0/M1 development and for headless verification.
/// </summary>
public static class CetusTrace
{
    private static readonly string LogPath =
        Path.Combine(CetusPaths.LogDir, "cetus-shell.log");

    public static void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // tracing must never break the shell
        }
    }
}
