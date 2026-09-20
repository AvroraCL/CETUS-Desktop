using System.IO;

namespace Cetus.Browser;

/// <summary>
/// The document-created script that bridges the Harness page and Cetus: theme
/// reporting, the CETUS settings group, and the update notice card.
///
/// It lives in its own file (embedded as a resource) so tests/bridge can
/// execute exactly the bytes the page receives. A script this size cannot be
/// validated by a C# unit test, and a syntax or property mistake in it silently
/// disables the whole bridge.
/// </summary>
internal static class BridgeScript
{
    internal const string ResourceName = "Cetus.BridgeScript.js";

    /// <summary>The script text, embedded from BridgeScript.js at build time.</summary>
    internal static string Source { get; } = ReadResource();

    private static string ReadResource()
    {
        using Stream? stream = typeof(BridgeScript).Assembly
            .GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"找不到内嵌脚本资源 {ResourceName}。");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
