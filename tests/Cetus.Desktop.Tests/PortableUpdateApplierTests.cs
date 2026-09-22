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
    public void WriteApplyScript_UsesTransactionalPowerShellAndHealthCheck()
    {
        using var directory = new TemporaryDirectory();
        string staging = System.IO.Path.Combine(directory.Path, "staging");
        Directory.CreateDirectory(staging);

        string script = PortableUpdateApplier.WriteApplyScript(
            staging,
            System.IO.Path.Combine(directory.Path, "target"),
            processId: 4242,
            zipPath: null,
            version: new Version(9, 9, 9));

        string content = File.ReadAllText(script);
        Assert.True(File.ReadAllBytes(script).AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.EndsWith(".ps1", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$oldPid = 4242", content, StringComparison.Ordinal);
        Assert.Contains("Read-ManagedFiles", content, StringComparison.Ordinal);
        Assert.Contains("Restore-Backup", content, StringComparison.Ordinal);
        Assert.Contains("--update-health=", content, StringComparison.Ordinal);

        // The health budget must cover Cetus's own worst-case startup path
        // (occupied-port grace + readiness wait + WebView2); a budget shorter
        // than that turns a slow first start into a rollback loop that kills DSH.
        Assert.Contains(
            $"$healthSeconds = {(int)PortableUpdateApplier.HealthBudget.TotalSeconds}",
            content,
            StringComparison.Ordinal);
        Assert.Contains("$attemptVersion = '9.9.9'", content, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json -Compress", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/MIR", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-Item -LiteralPath $PSCommandPath", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyScript_FailedNewExecutable_RollsBackAndPreservesUnknownFiles()
    {
        using var directory = new TemporaryDirectory();
        string staging = System.IO.Path.Combine(directory.Path, "暂存 更新 🧪");
        string target = System.IO.Path.Combine(directory.Path, "安装 目录 🐋");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(System.IO.Path.Combine(target, "runtime"));
        File.WriteAllText(System.IO.Path.Combine(staging, "Cetus.exe"), "new-exe");
        File.WriteAllText(System.IO.Path.Combine(staging, "new-file.txt"), "new");
        File.WriteAllText(System.IO.Path.Combine(target, "Cetus.exe"), "old-exe");
        File.WriteAllText(System.IO.Path.Combine(target, "user-file.txt"), "mine");
        File.WriteAllText(System.IO.Path.Combine(target, "runtime", "junk.txt"), "junk");
        WriteManifest(staging, "Cetus.exe", "new-file.txt", "runtime/VERSIONS.txt");
        WriteManifest(target, "Cetus.exe", "retired.txt", "runtime/junk.txt");
        File.WriteAllText(System.IO.Path.Combine(target, "retired.txt"), "old-managed");

        // A PID that cannot exist: the wait loop must fall straight through.
        string script = PortableUpdateApplier.WriteApplyScript(staging, target, processId: int.MaxValue);

        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }))
        {
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(30_000), "the takeover script did not finish");
        }

        Assert.Equal("old-exe", File.ReadAllText(System.IO.Path.Combine(target, "Cetus.exe")));
        Assert.Equal("mine", File.ReadAllText(System.IO.Path.Combine(target, "user-file.txt")));
        Assert.Equal("junk", File.ReadAllText(System.IO.Path.Combine(target, "runtime", "junk.txt")));
        Assert.Equal("old-managed", File.ReadAllText(System.IO.Path.Combine(target, "retired.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(target, "new-file.txt")));
        Assert.True(File.Exists(PortableUpdateApplier.FailureNoticePath));
    }

    [Fact]
    public void ApplyScript_MalformedOldManifest_ReportsFailureBeforeChangingTheInstallation()
    {
        using var directory = new TemporaryDirectory();
        string staging = System.IO.Path.Combine(directory.Path, "staging");
        string target = System.IO.Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        File.WriteAllText(System.IO.Path.Combine(staging, "Cetus.exe"), "new-exe");
        File.WriteAllText(System.IO.Path.Combine(target, "Cetus.exe"), "old-exe");
        File.WriteAllText(System.IO.Path.Combine(target, PortableUpdateApplier.ManagedFilesManifestName), "{invalid");
        WriteManifest(staging, "Cetus.exe");

        string script = PortableUpdateApplier.WriteApplyScript(
            staging, target, processId: int.MaxValue);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        using var process = System.Diagnostics.Process.Start(startInfo);

        Assert.NotNull(process);
        Assert.True(process.WaitForExit(30_000), "the takeover script did not finish");
        Assert.Equal(1, process.ExitCode);
        Assert.Equal("old-exe", File.ReadAllText(System.IO.Path.Combine(target, "Cetus.exe")));
        Assert.True(File.Exists(PortableUpdateApplier.FailureNoticePath));
        Assert.Contains("新版本升级失败", File.ReadAllText(PortableUpdateApplier.FailureNoticePath),
            StringComparison.Ordinal);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteManifest(string directory, params string[] files)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            fullyManagedDirectories = new[] { "runtime" },
            files = files.Append(PortableUpdateApplier.ManagedFilesManifestName).ToArray(),
        });
        File.WriteAllText(
            System.IO.Path.Combine(directory, PortableUpdateApplier.ManagedFilesManifestName),
            json,
            new UTF8Encoding(false));
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
