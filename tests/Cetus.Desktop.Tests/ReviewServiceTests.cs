using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class ReviewServiceTests : IDisposable
{
    private readonly string _root = TestWorkspace.CreateDirectory();

    [Fact]
    public void ParseStatus_PreservesNamesAndConsumesRenameSource()
    {
        IReadOnlyList<ReviewFile> files = ReviewService.ParseStatus(
            " M 中文.txt\0R  new name.txt\0old name.txt\0?? folder/a -> b.txt\0");
        Assert.Equal(new[] { "中文.txt", "new name.txt", "folder/a -> b.txt" }, files.Select(file => file.Path));
        Assert.Equal("R", files[1].Status);
    }

    [Fact]
    public async Task Changes_FromSubdirectoryResolveChineseAndNestedUntrackedFiles()
    {
        await GitRunner.RunAsync(_root, ["init", "-q"]);
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        string path = Path.Combine(sub, "中文.txt");
        await File.WriteAllTextAsync(path, "before\n");
        await GitRunner.RunAsync(_root, ["add", "."]);
        (int commitExit, _) = await GitRunner.RunAsync(_root,
            ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "initial"]);
        Assert.Equal(0, commitExit);
        await File.AppendAllTextAsync(path, "after\n");
        Directory.CreateDirectory(Path.Combine(sub, "new"));
        await File.WriteAllTextAsync(Path.Combine(sub, "new", "新增.txt"), "new content");

        (string root, IReadOnlyList<ReviewFile> files) = await ReviewService.GetChangesAsync(sub);
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(root), ignoreCase: true);
        ReviewFile modified = Assert.Single(files, file => file.Status == "M");
        Assert.Equal("sub/中文.txt", modified.Path);
        (int diffExit, string diff) = await GitRunner.RunAsync(root, ["diff", "HEAD", "--", modified.Path]);
        Assert.Equal(0, diffExit);
        Assert.Contains("+after", diff);
        ReviewFile untracked = Assert.Single(files, file => file.Status == "??");
        Assert.Equal("sub/new/新增.txt", untracked.Path);
        Assert.Contains(await ReviewService.ReadUntrackedAsync(root, untracked.Path), line => line.Text == "+new content");
    }

    [Fact]
    public async Task Untracked_BoundsVeryLongSingleLine()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "large.txt"), new string('a', ReviewService.MaxBytes * 4));
        IReadOnlyList<DiffLine> lines = await ReviewService.ReadUntrackedAsync(_root, "large.txt");
        Assert.True(Assert.Single(lines, line => line.Kind == "add").Text.Length < 4200);
        Assert.Contains(lines, line => line.Kind == "info" && line.Text.Contains("截断"));
    }

    [Fact]
    public async Task Untracked_BoundsLineCountAndRejectsBinary()
    {
        await File.WriteAllLinesAsync(Path.Combine(_root, "lines.txt"), Enumerable.Repeat("line", 1000));
        var lines = await ReviewService.ReadUntrackedAsync(_root, "lines.txt");
        Assert.Equal(800, lines.Count(line => line.Kind == "add"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "binary.dat"), [0, 1, 2, 3]);
        Assert.Contains("二进制", Assert.Single(await ReviewService.ReadUntrackedAsync(_root, "binary.dat")).Text);
    }

    [Fact]
    public async Task Untracked_MissingLockedAndOutsideFilesReturnNotices()
    {
        Assert.Equal("info", Assert.Single(await ReviewService.ReadUntrackedAsync(_root, "missing.txt")).Kind);
        Assert.Contains("无法解析", Assert.Single(await ReviewService.ReadUntrackedAsync(_root, "../outside.txt")).Text);
        using var locked = new FileStream(Path.Combine(_root, "locked.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Contains("读取文件失败", Assert.Single(await ReviewService.ReadUntrackedAsync(_root, "locked.txt")).Text);
    }

    public void Dispose()
    {
        if (!TestWorkspace.RetainArtifacts)
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
