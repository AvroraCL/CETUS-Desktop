using System.IO.Compression;
using Cetus.Platform;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DiagnosticsCollectorTests
{
    [Fact]
    public void BuildFailureReport_IncludesErrorPortAndVersion()
    {
        string report = DiagnosticsCollector.BuildFailureReport(
            "DSH 提前退出", hostLogPath: @"C:\logs\dsh-1.log", port: 4300, version: "0.3.0");

        Assert.Contains("0.3.0", report);
        Assert.Contains("4300", report);
        Assert.Contains(@"C:\logs\dsh-1.log", report);
        Assert.Contains("DSH 提前退出", report);
    }

    [Fact]
    public void ReadFileTail_ReturnsOnlyTheLastLines()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "sample.log");
        File.WriteAllLines(path, Enumerable.Range(1, 200).Select(index => $"line-{index}"));

        string tail = DiagnosticsCollector.ReadFileTail(path, maxLines: 10);

        Assert.DoesNotContain("line-190", tail, StringComparison.Ordinal);
        string[] lines = tail.Split('\n');
        Assert.Equal($"line-191", lines[0].TrimEnd('\r'));
        Assert.EndsWith($"line-200", tail);
    }

    [Fact]
    public void ReadLogTail_PrefersSidecarLogThenNewestFiles()
    {
        using var directory = new TemporaryDirectory();
        string sidecar = System.IO.Path.Combine(directory.Path, "sidecar.log");
        File.WriteAllText(sidecar, "sidecar-content");
        for (int index = 0; index < DiagnosticsCollector.MaxLogFiles + 1; index++)
        {
            string log = System.IO.Path.Combine(directory.Path, $"dsh-{index:00}.log");
            File.WriteAllText(log, $"content-{index}");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(-2 + index));
        }

        string tail = DiagnosticsCollector.ReadLogTail(sidecar, directory.Path, maxLinesPerFile: 10);

        Assert.Contains("sidecar.log", tail, StringComparison.Ordinal);
        Assert.Contains("sidecar-content", tail, StringComparison.Ordinal);
        // The newest directory log follows the preferred sidecar log.
        Assert.True(
            tail.IndexOf("sidecar.log", StringComparison.Ordinal)
            < tail.IndexOf("dsh-05.log", StringComparison.Ordinal));
        // Only MaxLogFiles newest directory logs are included.
        Assert.DoesNotContain("dsh-00.log", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadLogTail_WithoutAnyLogs_YieldsPlaceholder()
    {
        using var directory = new TemporaryDirectory();

        string tail = DiagnosticsCollector.ReadLogTail(null, directory.Path);

        Assert.Contains("没有可用", tail);
    }

    [Fact]
    public void BuildArchive_PacksReportSettingsLogsAndManifest()
    {
        using var directory = new TemporaryDirectory();
        string zipPath = System.IO.Path.Combine(directory.Path, "diag.zip");
        string settings = System.IO.Path.Combine(directory.Path, "settings.json");
        string log = System.IO.Path.Combine(directory.Path, "logs");
        Directory.CreateDirectory(log);
        string logFile = System.IO.Path.Combine(log, "app.log");
        File.WriteAllText(settings, """{ "Port": 4300 }""");
        File.WriteAllText(logFile, "log-line");
        string versions = System.IO.Path.Combine(directory.Path, "VERSIONS.txt");
        File.WriteAllText(versions, "cetus=0.3.0");

        DiagnosticsCollector.BuildArchive(zipPath, settings, log, versions, "report-body");

        using FileStream stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(
            ["diagnostics.txt", "settings.json", "runtime-VERSIONS.txt", "logs/app.log"],
            archive.Entries.Select(entry => entry.FullName));
        using var reader = new StreamReader(archive.Entries[0].Open());
        Assert.Equal("report-body", reader.ReadToEnd());
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
