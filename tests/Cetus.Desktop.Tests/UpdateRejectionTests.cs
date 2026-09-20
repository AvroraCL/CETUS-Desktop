using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// A failed portable update rolls back and relaunches the previous build. That
/// build must not immediately re-detect and reinstall the same release, because
/// every cycle kills the DSH host — the marker below is what breaks the loop.
/// </summary>
public sealed class UpdateRejectionTests
{
    [Fact]
    public void RecordAndIsRejected_RoundTrip()
    {
        using var scope = new UpdateDirectoryScope();

        Assert.False(UpdateRejection.IsRejected(new Version(0, 3, 3)));

        UpdateRejection.Record(new Version(0, 3, 3));

        Assert.True(UpdateRejection.IsRejected(new Version(0, 3, 3)));
        Assert.False(UpdateRejection.IsRejected(new Version(0, 3, 4)));
    }

    [Fact]
    public void Record_IsIdempotent()
    {
        using var scope = new UpdateDirectoryScope();

        UpdateRejection.Record(new Version(1, 2, 3));
        UpdateRejection.Record(new Version(1, 2, 3));

        string[] lines = File.ReadAllLines(UpdateRejection.FilePath)
            .Where(line => line.Trim().Length > 0)
            .ToArray();
        Assert.Single(lines);
    }

    [Fact]
    public void Clear_AllowsTheVersionAgain()
    {
        using var scope = new UpdateDirectoryScope();
        UpdateRejection.Record(new Version(2, 0, 0));
        UpdateRejection.Record(new Version(2, 0, 1));

        UpdateRejection.Clear(new Version(2, 0, 0));

        Assert.False(UpdateRejection.IsRejected(new Version(2, 0, 0)));
        Assert.True(UpdateRejection.IsRejected(new Version(2, 0, 1)));
    }

    [Fact]
    public void TryRead_ParsesJsonNoticeAndLegacyPlainText()
    {
        using var scope = new UpdateDirectoryScope();

        string jsonPath = Path.Combine(scope.Directory, "notice.json");
        File.WriteAllText(
            jsonPath,
            """{"version":"0.9.9","reason":"新版本升级失败，已恢复旧版本。详情：x","at":"2026-09-20T23:00:00+08:00"}""");
        PortableUpdateFailureNotice? parsed = PortableUpdateFailureNotice.TryRead(jsonPath);
        Assert.NotNull(parsed);
        Assert.Equal("0.9.9", parsed!.Version);
        Assert.Contains("详情", parsed.Reason);

        string legacyPath = Path.Combine(scope.Directory, "legacy.txt");
        File.WriteAllText(legacyPath, "新版本升级失败，已恢复旧版本。详情：旧格式");
        PortableUpdateFailureNotice? legacy = PortableUpdateFailureNotice.TryRead(legacyPath);
        Assert.NotNull(legacy);
        Assert.Null(legacy!.Version);
        Assert.Contains("旧格式", legacy.Reason);
    }

    private sealed class UpdateDirectoryScope : IDisposable
    {
        private readonly string? _original = Environment.GetEnvironmentVariable("CETUS_UPDATE_DIR");

        public UpdateDirectoryScope()
        {
            Directory = TestWorkspace.CreateDirectory();
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", Directory);
        }

        public string Directory { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", _original);
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
