using System.Diagnostics;
using System.Text;

namespace Cetus.Sidebar;

/// <summary>Runs read-only git commands inside a working directory.</summary>
internal static class GitRunner
{
    /// <summary>
    /// Returns the process exit code and UTF-8 standard output. Throws
    /// <see cref="System.ComponentModel.Win32Exception"/> when git is absent.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }
}
