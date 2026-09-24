using System.Diagnostics;
using Cetus.Browser;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// The bridge script is injected as a string into the Harness page: a syntax or
/// property mistake in it silently disables the theme bridge, the settings group
/// and the update notice, and no C# assertion would notice. These tests run the
/// exact embedded bytes against a DOM shim (tests/bridge) so the script is
/// executed, not merely inspected.
/// </summary>
public sealed class BridgeScriptTests
{
    [Fact]
    public void EmbeddedScript_IsPresentAndCarriesEveryBridgeMessage()
    {
        string script = BridgeScript.Source;

        Assert.True(script.Length > 5000, $"脚本过短：{script.Length}");
        Assert.StartsWith("(() => {", script, StringComparison.Ordinal);
        Assert.EndsWith("})();", script.TrimEnd(), StringComparison.Ordinal);

        string[] required =
        [
            "cetus-settings-state",
            "cetus-settings-request",
            "cetus-setting-changed",
            "cetus-open-port-settings",
            "cetus-check-updates",
            "cetus-check-dsh-update",
            "cetus-update-state",
            "cetus-update-state-request",
            "cetus-update-install",
            "cetus-update-details",
            "cetus-update-dismiss",
            "cetus-update-card",
        ];

        foreach (string token in required)
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EmbeddedScript_RendersTheUpdateCardUnderADomShim()
    {
        string? node = FindNodeExecutable();
        if (node is null)
        {
            return; // No node available: covered by the repo-level run documented in docs/dev.
        }

        string runner = Path.Combine(FindBridgeDirectory(), "run-bridge-test.js");
        Assert.True(File.Exists(runner), $"找不到桥脚本测试器：{runner}");

        string scriptPath = Path.Combine(Path.GetTempPath(), $"cetus-bridge-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(scriptPath, BridgeScript.Source);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = node,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(runner);
            startInfo.ArgumentList.Add(scriptPath);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 node 运行桥脚本测试。");
            string output = await process.StandardOutput.ReadToEndAsync();
            string errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(
                process.ExitCode == 0,
                $"桥脚本在 DOM 垫片上执行失败（exit {process.ExitCode}）。\n{output}\n{errors}");
            Assert.Contains("ALL BRIDGE CHECKS PASSED", output, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Walks up from the test output directory to the repository root.</summary>
    private static string FindBridgeDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "bridge");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("无法从测试输出目录定位 tests/bridge。");
    }

    private static string? FindNodeExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("CETUS_TEST_NODE_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe"),
            @"C:\Program Files\nodejs\node.exe",
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(entry.Trim('"'), "node.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
