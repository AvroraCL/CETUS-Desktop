using System.IO;
using System.Text;
using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class FilePreviewServiceTests
{
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [Fact]
    public void Classify_PlainUtf8WithoutBomIsText()
    {
        (FilePreviewKind kind, _) = FilePreviewService.Classify(
            "notes.txt",
            Encoding.UTF8.GetBytes("hello CETUS\nsecond line"));

        Assert.Equal(FilePreviewKind.Text, kind);
    }

    [Fact]
    public void Classify_Utf16LeBomIsText()
    {
        (FilePreviewKind kind, Encoding encoding) = FilePreviewService.Classify(
            "legacy.csv",
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a,b")).ToArray());

        Assert.Equal(FilePreviewKind.Text, kind);
        Assert.Equal(Encoding.Unicode.CodePage, encoding.CodePage);
    }

    [Fact]
    public void Classify_Utf8BomIsText()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, (byte)'h'];
        (FilePreviewKind kind, _) = FilePreviewService.Classify("x.md", withBom);
        Assert.Equal(FilePreviewKind.Text, kind);
    }

    [Fact]
    public void Classify_NullByteIsBinary()
    {
        (FilePreviewKind kind, _) = FilePreviewService.Classify(
            "blob.dat",
            [0x50, 0x4B, 0x00, 0x03]);

        Assert.Equal(FilePreviewKind.Unknown, kind);
    }

    [Fact]
    public void Classify_InvalidUtf8IsBinary()
    {
        (FilePreviewKind kind, _) = FilePreviewService.Classify(
            "mixed.log",
            [0x61, 0xC3, 0x28, 0x62]);

        Assert.Equal(FilePreviewKind.Unknown, kind);
    }

    [Fact]
    public void Classify_ImageAndSvgExtensions()
    {
        Assert.Equal(
            FilePreviewKind.Image,
            FilePreviewService.Classify("art.png", []).Kind);
        Assert.Equal(
            FilePreviewKind.Svg,
            FilePreviewService.Classify("icon.svg", "<svg/>"u8.ToArray()).Kind);
    }

    [Fact]
    public async Task LoadAsync_TruncatesOversizedText()
    {
        string root = TestWorkspace.CreateDirectory();
        try
        {
            string path = Path.Combine(root, "big.txt");
            await File.WriteAllLinesAsync(
                path,
                Enumerable.Range(0, FilePreviewService.MaxLines + 100).Select(i => $"line {i}"));

            FilePreviewResult result = await FilePreviewService.LoadAsync(path, CancellationToken.None);

            Assert.Equal(FilePreviewContent.Text, result.Content);
            Assert.True(result.IsTruncated);
            Assert.Equal(FilePreviewService.MaxLines + 1, result.Lines!.Count);
            Assert.True(result.Lines[^1].IsNotice);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_ReportsBinaryNotice()
    {
        string root = TestWorkspace.CreateDirectory();
        try
        {
            string path = Path.Combine(root, "blob.bin");
            await File.WriteAllBytesAsync(path, [0x01, 0x00, 0x02, 0x00]);

            FilePreviewResult result = await FilePreviewService.LoadAsync(path, CancellationToken.None);

            Assert.Equal(FilePreviewContent.Notice, result.Content);
            Assert.False(string.IsNullOrWhiteSpace(result.Notice));
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_DecodesPngImage()
    {
        string root = TestWorkspace.CreateDirectory();
        try
        {
            string path = Path.Combine(root, "dot.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(TinyPngBase64));

            FilePreviewResult result = await FilePreviewService.LoadAsync(path, CancellationToken.None);

            Assert.Equal(FilePreviewContent.Image, result.Content);
            Assert.NotNull(result.Image);
            Assert.False(result.IsTruncated);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_EmptyFileShowsNotice()
    {
        string root = TestWorkspace.CreateDirectory();
        try
        {
            string path = Path.Combine(root, "empty.txt");
            await File.WriteAllTextAsync(path, string.Empty);

            FilePreviewResult result = await FilePreviewService.LoadAsync(path, CancellationToken.None);

            Assert.Equal(FilePreviewContent.Notice, result.Content);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_MissingFileShowsNotice()
    {
        FilePreviewResult result = await FilePreviewService.LoadAsync(
            Path.Combine(Path.GetTempPath(), $"cetus-missing-{Guid.NewGuid():N}.txt"),
            CancellationToken.None);

        Assert.Equal(FilePreviewContent.Notice, result.Content);
    }
}
