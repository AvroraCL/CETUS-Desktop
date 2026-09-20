using Cetus.Configuration;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class LaunchArgsTests
{
    [Fact]
    public void Parse_EmptyArgs_YieldsDefaults()
    {
        LaunchRequest request = LaunchArgs.Parse([]);

        Assert.False(request.StartInBackground);
        Assert.Null(request.WorkspacePath);
        Assert.Null(request.UpdateHealthPath);
    }

    [Fact]
    public void Parse_UpdateHealth_AcceptsOnlyRandomFileBelowUpdateCache()
    {
        string valid = System.IO.Path.Combine(
            CetusPaths.UpdateCacheDirectory,
            $"update-health-{Guid.NewGuid():N}.ready");
        string validNameOutsideCache = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(CetusPaths.UpdateCacheDirectory)!,
            "outside",
            $"update-health-{Guid.NewGuid():N}.ready");

        Assert.Equal(valid, LaunchArgs.Parse([$"--update-health={valid}"]).UpdateHealthPath);
        Assert.Null(LaunchArgs.Parse([$"--update-health={validNameOutsideCache}"]).UpdateHealthPath);
        Assert.Null(LaunchArgs.Parse([$"--update-health={System.IO.Path.Combine(CetusPaths.UpdateCacheDirectory, "predictable.ready")}"]).UpdateHealthPath);
    }

    [Fact]
    public void Parse_BackgroundFlag_IsRecognizedCaseInsensitively()
    {
        Assert.True(LaunchArgs.Parse(["--BACKGROUND"]).StartInBackground);
        Assert.True(LaunchArgs.Parse(["--background"]).StartInBackground);
    }

    [Fact]
    public void Parse_ExistingDirectory_BecomesWorkspacePath()
    {
        using var directory = new TemporaryDirectory();

        LaunchRequest request = LaunchArgs.Parse([directory.Path]);

        Assert.False(request.StartInBackground);
        Assert.Equal(System.IO.Path.GetFullPath(directory.Path), request.WorkspacePath);
    }

    [Fact]
    public void Parse_NonDirectoryArgument_IsIgnored()
    {
        Assert.Null(LaunchArgs.Parse(["F:\\definitely-not-a-cetus-workspace"]).WorkspacePath);
        Assert.Null(LaunchArgs.Parse(["--unknown-flag"]).WorkspacePath);
    }

    [Fact]
    public void Parse_ProtocolUrl_ExtractsDecodedPath()
    {
        using var directory = new TemporaryDirectory();
        string encoded = Uri.EscapeDataString(directory.Path);

        LaunchRequest request = LaunchArgs.Parse([$"cetus://open?path={encoded}"]);

        Assert.Equal(System.IO.Path.GetFullPath(directory.Path), request.WorkspacePath);
    }

    [Fact]
    public void Parse_ProtocolUrlWithMissingPath_IsIgnored()
    {
        Assert.Null(LaunchArgs.Parse(["cetus://open"]).WorkspacePath);
        Assert.Null(LaunchArgs.Parse(["cetus://open?path=F:\\definitely-not-a-cetus-workspace"]).WorkspacePath);
    }

    [Fact]
    public void Parse_CombinesBackgroundAndWorkspace()
    {
        using var directory = new TemporaryDirectory();

        LaunchRequest request = LaunchArgs.Parse(["--background", directory.Path]);

        Assert.True(request.StartInBackground);
        Assert.NotNull(request.WorkspacePath);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestWorkspace.CreateDirectory();
        }

        public string Path { get; }

        public void Dispose()
        {
            if (TestWorkspace.RetainArtifacts) return;
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Leave failed-test artifacts for diagnosis.
            }
        }
    }
}
