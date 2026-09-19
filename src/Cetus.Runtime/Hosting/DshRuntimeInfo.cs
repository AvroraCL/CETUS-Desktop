using System.IO;

namespace Cetus.Hosting;

/// <summary>
/// Locates the version of the DSH runtime bundled with this build. Packaged
/// builds record it in <c>runtime\VERSIONS.txt</c> next to the executable;
/// development builds have no bundled runtime and report null.
/// </summary>
public static class DshRuntimeInfo
{
    public static string? ReadInstalledVersion(string? appDirectory = null)
    {
        string directory = appDirectory ?? AppContext.BaseDirectory;
        return ReadVersionsFile(Path.Combine(directory, "runtime", "VERSIONS.txt"));
    }

    public static string? ReadVersionsFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadLines(path))
            {
                // Lines look like "dsh=0.1.6-alpha.2".
                if (line.StartsWith("dsh=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = line[4..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }
}
