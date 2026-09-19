using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Text;

namespace Cetus.Platform;

/// <summary>
/// Builds the safe-mode failure report and the exportable diagnostics
/// archive. Pure-enough to test: IO is parameterized by paths, nothing here
/// touches WPF state.
/// </summary>
internal static class DiagnosticsCollector
{
    public const int MaxLogFiles = 5;

    public static string BuildFailureReport(
        string error,
        string? hostLogPath,
        int port,
        string version)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CETUS 版本   : {version}");
        builder.AppendLine($"系统        : {Environment.OSVersion.VersionString}");
        builder.AppendLine($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"DSH 端口    : {port}（{(IsPortListening(port) ? "被其他程序监听" : "空闲或不可探测")}）");
        builder.AppendLine($"侧边车日志  : {(string.IsNullOrWhiteSpace(hostLogPath) ? "（本次未生成）" : hostLogPath)}");
        builder.AppendLine();
        builder.AppendLine("错误");
        builder.AppendLine(error);
        return builder.ToString();
    }

    public static bool IsPortListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    /// <summary>
    /// Concatenates the tail of the preferred sidecar log and the newest log
    /// files in the log directory, oldest last.
    /// </summary>
    public static string ReadLogTail(string? preferredPath, string logDirectory, int maxLinesPerFile = 80)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            candidates.Add(preferredPath);
        }

        try
        {
            if (Directory.Exists(logDirectory))
            {
                candidates.AddRange(Directory
                    .EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(MaxLogFiles));
            }
        }
        catch (IOException)
        {
        }

        var builder = new StringBuilder();
        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"===== {path} =====");
            builder.AppendLine(ReadFileTail(path, maxLinesPerFile));
            builder.AppendLine();
        }

        return builder.Length == 0 ? "（没有可用的日志文件。）" : builder.ToString().TrimEnd();
    }

    public static string ReadFileTail(string path, int maxLines)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            Queue<string> tail = new(maxLines);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (tail.Count == maxLines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);
            }

            return string.Join(Environment.NewLine, tail);
        }
        catch (IOException error)
        {
            return $"（读取失败：{error.Message}）";
        }
        catch (UnauthorizedAccessException)
        {
            return "（读取失败：没有权限。）";
        }
    }

    /// <summary>
    /// Zips settings, recent logs, the runtime manifest and a human-readable
    /// report into <paramref name="zipPath"/>. Credentials are never included.
    /// </summary>
    public static void BuildArchive(
        string zipPath,
        string? settingsPath,
        string logDirectory,
        string? runtimeVersionsPath,
        string reportText)
    {
        string? directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = new(zipPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        AddEntry(archive, "diagnostics.txt", reportText);

        if (!string.IsNullOrWhiteSpace(settingsPath)
            && File.Exists(settingsPath)
            && !ContainsCredentialsMarker(settingsPath))
        {
            archive.CreateEntryFromFile(settingsPath, "settings.json");
        }

        if (!string.IsNullOrWhiteSpace(runtimeVersionsPath) && File.Exists(runtimeVersionsPath))
        {
            archive.CreateEntryFromFile(runtimeVersionsPath, "runtime-VERSIONS.txt");
        }

        try
        {
            if (Directory.Exists(logDirectory))
            {
                foreach (string log in Directory
                    .EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(MaxLogFiles))
                {
                    archive.CreateEntryFromFile(log, $"logs/{Path.GetFileName(log)}");
                }
            }
        }
        catch (IOException)
        {
            // The report above still carries the log tails.
        }
    }

    /// <summary>
    /// Hard exclusion: DSH's credentials file (or anything named like it)
    /// never enters a diagnostics archive, even if future collection logic
    /// starts enumerating wider than today's whitelist.
    /// </summary>
    public static bool ContainsCredentialsMarker(string path) =>
        Path.GetFileName(path).Contains("credentials", StringComparison.OrdinalIgnoreCase);

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
