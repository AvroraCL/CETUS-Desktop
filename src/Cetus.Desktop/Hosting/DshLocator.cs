using System.Diagnostics;
using System.IO;

namespace Cetus.Hosting;

/// <summary>
/// Resolution result for the DSH host command: the node executable and the dsh entry script.
/// When only the <c>dsh</c> shim is found on PATH, <see cref="UseShim"/> is set and
/// <see cref="NodeExe"/>/<see cref="EntryScript"/> are empty (the shim is invoked via cmd).
/// </summary>
public sealed record DshCommand(string? NodeExe, string? EntryScript, bool UseShim);

/// <summary>
/// Locates node.exe and the dsh entry script. Probe order:
/// 1. bundled with the package (<c>runtime\node.exe</c> + <c>runtime\dsh\…\lib\bin.js</c>);
/// 2. <c>CETUS_NODE_EXE</c> / <c>CETUS_DSH_ENTRY</c> environment overrides;
/// 3. PATH scan for node.exe;
/// 4. npm global layout (<c>%APPDATA%\npm\node_modules\@deepseek-ai\dsh\lib\bin.js</c>);
/// 5. fallback: <c>dsh</c> shim on PATH, invoked through cmd.
/// </summary>
public static class DshLocator
{
    public const string DefaultEntry =
        @"node_modules\@deepseek-ai\dsh\lib\bin.js";

    public static DshCommand Resolve()
    {
        // 1) Bundled runtime (packaged builds): node.exe + dsh pinned next to the exe.
        //    Layout: runtime\node.exe + runtime\dsh\node_modules\@deepseek-ai\dsh\lib\bin.js
        string appDir = AppContext.BaseDirectory;
        string bundledNode = Path.Combine(appDir, "runtime", "node.exe");
        string bundledEntry = Path.Combine(appDir, "runtime", "dsh", DefaultEntry);
        if (File.Exists(bundledNode) && File.Exists(bundledEntry))
        {
            return new DshCommand(bundledNode, bundledEntry, UseShim: false);
        }

        // 2) Environment overrides.
        string? nodeExe = Environment.GetEnvironmentVariable("CETUS_NODE_EXE");
        nodeExe ??= FindOnPath("node.exe");

        string? entry = Environment.GetEnvironmentVariable("CETUS_DSH_ENTRY");
        entry ??= ProbeNpmGlobalEntry();

        if (nodeExe is not null && entry is not null && File.Exists(nodeExe) && File.Exists(entry))
        {
            return new DshCommand(nodeExe, entry, UseShim: false);
        }

        // Fallback: let the dsh.cmd / dsh shim resolve everything (slower, hidden cmd window).
        if (FindOnPath("dsh.cmd") is not null || FindOnPath("dsh") is not null)
        {
            return new DshCommand(null, null, UseShim: true);
        }

        throw new InvalidOperationException(
            "找不到 DSH：请确认已安装 @deepseek-ai/dsh，或设置 CETUS_DSH_ENTRY / CETUS_NODE_EXE。");
    }

    private static string? ProbeNpmGlobalEntry()
    {
        // Default npm global layout on Windows.
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string candidate = Path.Combine(appData, "npm", DefaultEntry);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Program Files layout used by some package managers.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidate = Path.Combine(programFiles, "nodejs", DefaultEntry);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
