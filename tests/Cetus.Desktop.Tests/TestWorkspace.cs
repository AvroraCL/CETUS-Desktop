namespace Cetus.Desktop.Tests;

internal static class TestWorkspace
{
    public static bool RetainArtifacts =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CETUS_TEST_ROOT"));

    public static string CreateDirectory()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("CETUS_TEST_ROOT");
        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Path.GetTempPath(), "CetusTests")
            : Path.GetFullPath(configuredRoot);
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
