using System.IO;

namespace Cetus.Desktop.Core;

/// <summary>
/// Well-known paths for the packaged / running shell.
/// Order of preference for the node + dsh runtimes:
///   1. bundled inside the app directory (runtime\...)  ← packaged build
///   2. environment overrides (CETUS_NODE_EXE / CETUS_DSH_CLI_JS)
///   3. machine-wide discovery (Program Files / PATH / npm global roots)
/// </summary>
public static class CetusPaths
{
    /// <summary>Directory that contains the running executable.</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>Bundled node.exe (packaged builds only).</summary>
    public static string BundledNodeExe => Path.Combine(AppDir, "runtime", "node.exe");

    /// <summary>Bundled dsh CLI entry (packaged builds only).</summary>
    public static string BundledDshCliJs => Path.Combine(
        AppDir, "runtime", "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

    /// <summary>Shell logs + sidecar logs live here (per-user, no admin needed).</summary>
    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cetus", "logs");

    /// <summary>Isolated WebView2 profile data (cache, cookies, local storage).</summary>
    public static string WebView2DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cetus", "WebView2");
}
