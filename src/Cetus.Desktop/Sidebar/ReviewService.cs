using System.IO;
using System.Text;

namespace Cetus.Sidebar;

internal static class ReviewService
{
    internal const int MaxBytes = 512 * 1024;
    internal const int MaxLines = 800;

    public static async Task<(string Root, IReadOnlyList<ReviewFile> Files)> GetChangesAsync(string cwd)
    {
        (int rootExit, string rootOutput) = await GitRunner.RunAsync(cwd, ["rev-parse", "--show-toplevel"]);
        if (rootExit != 0)
        {
            throw new InvalidOperationException("当前会话目录不是 git 仓库，无法审查改动。");
        }

        string root = rootOutput.TrimEnd('\r', '\n');
        (int statusExit, string output) = await GitRunner.RunAsync(root,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"]);
        if (statusExit != 0)
        {
            throw new InvalidOperationException("git status 执行失败，请确认该目录可读。");
        }

        return (root, ParseStatus(output));
    }

    internal static IReadOnlyList<ReviewFile> ParseStatus(string output)
    {
        string[] records = output.Split('\0');
        var files = new List<ReviewFile>();
        for (int i = 0; i < records.Length; i++)
        {
            string record = records[i];
            if (record.Length < 4)
            {
                continue;
            }

            string status = record[..2];
            files.Add(new ReviewFile(status.Trim(), record[3..]));
            // With -z, rename/copy records contain destination then source.
            if (status.Contains('R') || status.Contains('C'))
            {
                i++;
            }
        }

        return files;
    }

    public static async Task<IReadOnlyList<DiffLine>> ReadUntrackedAsync(string root, string relativePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return [DiffLine.Info("无法解析该未跟踪文件在工作区中的路径。")];
            }

            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[MaxBytes + 1];
            int count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
            bool truncated = count > MaxBytes;
            count = Math.Min(count, MaxBytes);
            (FilePreviewKind kind, Encoding encoding) = FilePreviewService.Classify("text", buffer.AsSpan(0, count));
            if (kind != FilePreviewKind.Text)
            {
                return [DiffLine.Info("二进制文件，无法显示 diff。")];
            }

            var lines = new List<DiffLine> { new("hunk", $"@@ 未跟踪文件（全部视为新增）: {relativePath} @@") };
            using var reader = new StringReader(encoding.GetString(buffer, 0, count).TrimStart('\uFEFF'));
            string? line;
            int lineCount = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if (lineCount++ >= MaxLines)
                {
                    truncated = true;
                    break;
                }

                if (line.Length > FilePreviewService.MaxLineChars)
                {
                    line = line[..FilePreviewService.MaxLineChars] + " …";
                    truncated = true;
                }

                lines.Add(new DiffLine("add", "+" + line));
            }

            if (truncated)
            {
                lines.Add(DiffLine.Info("… 文件过大，已截断（最多 800 行 / 512 KB）"));
            }

            return lines;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [DiffLine.Info($"读取文件失败：{error.Message}")];
        }
    }
}
