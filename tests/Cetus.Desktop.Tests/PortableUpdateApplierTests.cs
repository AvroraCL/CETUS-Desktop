using System.IO;
using System.IO.Compression;
using System.Text;
using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class PortableUpdateApplierTests : IDisposable
{
    private readonly string? _originalUpdateDir;

    public PortableUpdateApplierTests()
    {
        // Applier paths derive from CetusPaths.UpdateCacheDirectory; keep
        // tests away from the real %LOCALAPPDATA% cache.
        _originalUpdateDir = Environment.GetEnvironmentVariable("CETUS_UPDATE_DIR");
        Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", TestWorkspace.CreateDirectory());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", _originalUpdateDir);
    }

    private static string CreateBundle(string directory, bool withExecutable)
    {
        string zipPath = System.IO.Path.Combine(directory, "bundle.zip");
        using FileStream stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        if (withExecutable)
        {
            AddEntry(archive, "Cetus.exe", "fake-exe");
        }

        AddEntry(archive, "runtime/VERSIONS.txt", "cetus=9.9.9");
        AddEntry(archive, "runtime/dsh/placeholder.txt", "tree");
        return zipPath;
    }

    [Fact]
    public void PrepareStaging_ExtractsBundleAndValidatesExecutable()
    {
        using var directory = new TemporaryDirectory();
        string zipPath = CreateBundle(directory.Path, withExecutable: true);

        string staging = PortableUpdateApplier.PrepareStaging(zipPath, new Version(9, 9, 9));

        Assert.Equal(
            System.IO.Path.Combine(
                Cetus.Configuration.CetusPaths.UpdateCacheDirectory, "staging-9.9.9"),
            staging);
        Assert.True(File.Exists(System.IO.Path.Combine(staging, "Cetus.exe")));
        Assert.True(File.Exists(System.IO.Path.Combine(staging, "runtime", "VERSIONS.txt")));
    }

    [Fact]
    public void PrepareStaging_ReplacesAnOldStagingDirectory()
    {
        using var directory = new TemporaryDirectory();
        string zipPath = CreateBundle(directory.Path, withExecutable: true);
        Version version = new(9, 9, 9);
        string stale = System.IO.Path.Combine(
            Cetus.Configuration.CetusPaths.UpdateCacheDirectory, "staging-9.9.9", "stale-marker.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "old");

        string staging = PortableUpdateApplier.PrepareStaging(zipPath, version);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(System.IO.Path.Combine(staging, "Cetus.exe")));
    }

    [Fact]
    public void PrepareStaging_BundleWithoutExecutable_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        string zipPath = CreateBundle(directory.Path, withExecutable: false);

        Assert.Throws<InvalidOperationException>(
            () => PortableUpdateApplier.PrepareStaging(zipPath, new Version(9, 9, 9)));
    }

    [Fact]
    public void WriteApplyScript_WaitsMirrorsRelaunchesAndSelfDeletes()
    {
        using var directory = new TemporaryDirectory();
        string staging = System.IO.Path.Combine(directory.Path, "staging");
        Directory.CreateDirectory(staging);

        string script = PortableUpdateApplier.WriteApplyScript(
            staging, System.IO.Path.Combine(directory.Path, "target"), processId: 4242);

        string content = File.ReadAllText(script);
        Assert.Contains("tasklist /FI \"PID eq 4242\"", content, StringComparison.Ordinal);
        Assert.Contains("taskkill /PID 4242 /F", content, StringComparison.Ordinal);
        Assert.Contains("robocopy", content, StringComparison.Ordinal);
        Assert.Contains("/MIR", content, StringComparison.Ordinal);
        // Staging (hundreds of MB) must not outlive a successful apply.
        Assert.Contains($"rd /s /q \"{staging}\"", content, StringComparison.Ordinal);
        Assert.Contains("Cetus.exe", content, StringComparison.Ordinal);
        Assert.EndsWith("del \"%~f0\"", content.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyScript_EndToEnd_MirrorsTargetAndCleansUp()
    {
        using var directory = new TemporaryDirectory();
        string staging = System.IO.Path.Combine(directory.Path, "staging-9.9.9");
        string target = System.IO.Path.Combine(directory.Path, "install");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(System.IO.Path.Combine(target, "runtime"));
        File.WriteAllText(System.IO.Path.Combine(staging, "Cetus.exe"), "new-exe");
        File.WriteAllText(System.IO.Path.Combine(staging, "new-file.txt"), "new");
        File.WriteAllText(System.IO.Path.Combine(target, "old-file.txt"), "old");
        File.WriteAllText(System.IO.Path.Combine(target, "runtime", "junk.txt"), "junk");

        // A PID that cannot exist: the wait loop must fall straight through.
        string script = PortableUpdateApplier.WriteApplyScript(staging, target, processId: int.MaxValue);

        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }))
        {
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(30_000), "the takeover script did not finish");
        }

        // The mirror replaced the old tree content and dropped retired files.
        Assert.True(File.Exists(System.IO.Path.Combine(target, "Cetus.exe")));
        Assert.True(File.Exists(System.IO.Path.Combine(target, "new-file.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(target, "old-file.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(target, "runtime", "junk.txt")));
        // Cleanup: staging removed, script self-deleted.
        Assert.False(Directory.Exists(staging));
        Assert.False(File.Exists(script));
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
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
