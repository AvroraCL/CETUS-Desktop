using Cetus.Configuration;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class RuntimeLogTests
{
    [Fact]
    public void Append_WritesTimestampedLinesToTheLogDirectory()
    {
        using var directory = new TemporaryDirectory();
        string? original = Environment.GetEnvironmentVariable("CETUS_LOG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CETUS_LOG_DIR", directory.Path);

            RuntimeLog.Append("hello log");

            string logFile = Assert.Single(Directory.GetFiles(directory.Path, "cetus-*.log"));
            string content = File.ReadAllText(logFile);
            Assert.Contains("hello log", content, StringComparison.Ordinal);
            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} hello log", content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_LOG_DIR", original);
        }
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
