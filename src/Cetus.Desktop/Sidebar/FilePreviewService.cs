using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Cetus.Sidebar;

internal enum FilePreviewKind
{
    Text,
    Image,
    Svg,
    Unknown,
}

internal enum FilePreviewContent
{
    Text,
    Image,
    Notice,
}

internal sealed record FilePreviewLine(string Text, bool IsNotice = false);

internal sealed record FilePreviewResult(
    FilePreviewContent Content,
    IReadOnlyList<FilePreviewLine>? Lines,
    ImageSource? Image,
    string Notice,
    bool IsTruncated,
    long FileSize)
{
    public static FilePreviewResult CreateNotice(string text, long fileSize = 0) =>
        new(FilePreviewContent.Notice, null, null, text, false, fileSize);
}

/// <summary>
/// Bounded, cancellation-aware inline preview loading for the file panel:
/// content sniffing (BOM/NUL/strict UTF-8) decides text versus binary, text
/// streams line by line under hard byte/line caps, and images decode at a
/// panel-friendly width into frozen sources.
/// </summary>
internal static class FilePreviewService
{
    public const long MaxTextBytes = 512 * 1024;
    public const int MaxLines = 4000;
    public const int MaxLineChars = 4096;
    private const int SniffLength = 8 * 1024;
    private const int DecodeWidth = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<FilePreviewResult> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return FilePreviewResult.CreateNotice("文件不存在或已被移动。");
        }

        byte[] prefix = ReadPrefix(path, SniffLength);
        (FilePreviewKind kind, Encoding encoding) = Classify(path, prefix);
        return kind switch
        {
            FilePreviewKind.Text => await ReadTextAsync(path, info.Length, encoding, cancellationToken),
            FilePreviewKind.Image => await LoadImageAsync(path, cancellationToken),
            FilePreviewKind.Svg => await LoadSvgAsync(path, cancellationToken),
            _ => FilePreviewResult.CreateNotice("二进制文件，暂不支持预览。", info.Length),
        };
    }

    /// <summary>
    /// Classifies a file from its extension plus a content prefix. Text-like
    /// extensions and unknown extensions fall through to content sniffing, so
    /// extensionless sources (LICENSE, Makefile, .gitignore…) preview too.
    /// </summary>
    public static (FilePreviewKind Kind, Encoding Encoding) Classify(string path, ReadOnlySpan<byte> prefix)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico":
                return (FilePreviewKind.Image, Encoding.UTF8);
            case ".svg":
                return (FilePreviewKind.Svg, Encoding.UTF8);
        }

        if (DetectBom(prefix) is { } bom)
        {
            return (FilePreviewKind.Text, bom);
        }

        foreach (byte value in prefix)
        {
            if (value == 0)
            {
                return (FilePreviewKind.Unknown, Encoding.UTF8);
            }
        }

        // Strict UTF-8 check; skip an incomplete trailing sequence so a
        // character split across the prefix boundary cannot fail the file.
        int end = TrimIncompleteTail(prefix);
        try
        {
            _ = StrictUtf8.GetString(prefix[..end]);
            return (FilePreviewKind.Text, Encoding.UTF8);
        }
        catch (DecoderFallbackException)
        {
            return (FilePreviewKind.Unknown, Encoding.UTF8);
        }
    }

    private static int TrimIncompleteTail(ReadOnlySpan<byte> prefix)
    {
        int end = prefix.Length;
        if (end == 0)
        {
            return end;
        }

        int start = Math.Max(0, end - 4);
        for (int lead = end - 1; lead >= start; lead--)
        {
            byte value = prefix[lead];
            if ((value & 0xC0) == 0x80)
            {
                continue; // Continuation byte; keep walking back.
            }

            int expected = (value & 0xE0) == 0xC0 ? 2
                : (value & 0xF0) == 0xE0 ? 3
                : (value & 0xF8) == 0xF0 ? 4
                : 1;
            return end - lead < expected ? lead : end;
        }

        return end;
    }

    private static Encoding? DetectBom(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 4)
        {
            if (prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0 && prefix[3] == 0)
            {
                return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
            }

            if (prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xFE && prefix[3] == 0xFF)
            {
                return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
            }
        }

        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (prefix.Length >= 2)
        {
            if (prefix[0] == 0xFF && prefix[1] == 0xFE)
            {
                return Encoding.Unicode;
            }

            if (prefix[0] == 0xFE && prefix[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }
        }

        return null;
    }

    private static async Task<FilePreviewResult> ReadTextAsync(
        string path,
        long fileSize,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);
        var lines = new List<FilePreviewLine>();
        bool truncated = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (lines.Count >= MaxLines || stream.Position > MaxTextBytes)
            {
                truncated = true;
                break;
            }

            if (line.Length > MaxLineChars)
            {
                line = line[..MaxLineChars] + " …";
                truncated = true;
            }

            lines.Add(new FilePreviewLine(line));
        }

        if (lines.Count == 0)
        {
            return new FilePreviewResult(
                FilePreviewContent.Notice, null, null, "空文件。", truncated, fileSize);
        }

        if (truncated)
        {
            lines.Add(new FilePreviewLine($"… 预览已截断（{lines.Count} 行）", IsNotice: true));
        }

        return new FilePreviewResult(FilePreviewContent.Text, lines, null, string.Empty, truncated, fileSize);
    }

    private static async Task<FilePreviewResult> LoadImageAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        try
        {
            ImageSource image = await Task.Run(
                () =>
                {
                    var bitmap = new BitmapImage();
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = DecodeWidth;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return (ImageSource)bitmap;
                },
                cancellationToken);
            return new FilePreviewResult(FilePreviewContent.Image, null, image, string.Empty, false, info.Length);
        }
        catch (Exception error) when (
            error is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            return FilePreviewResult.CreateNotice("无法解码该图片格式。", info.Length);
        }
    }

    private static async Task<FilePreviewResult> LoadSvgAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        try
        {
            ImageSource image = await Task.Run(
                () =>
                {
                    var reader = new FileSvgReader(new WpfDrawingSettings { TextAsGeometry = false });
                    DrawingGroup drawing = reader.Read(path)
                        ?? throw new InvalidOperationException("SVG 渲染结果为空。");
                    drawing.Freeze();
                    var drawingImage = new DrawingImage(drawing);
                    drawingImage.Freeze();
                    return (ImageSource)drawingImage;
                },
                cancellationToken);
            return new FilePreviewResult(FilePreviewContent.Image, null, image, string.Empty, false, info.Length);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return FilePreviewResult.CreateNotice("无法渲染该 SVG。", info.Length);
        }
    }

    private static byte[] ReadPrefix(string path, int length)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            length,
            FileOptions.SequentialScan);
        var buffer = new byte[Math.Min((long)length, stream.Length)];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        if (read != buffer.Length)
        {
            Array.Resize(ref buffer, read);
        }

        return buffer;
    }
}
