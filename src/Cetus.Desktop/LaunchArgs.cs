using System.IO;
using Cetus.Configuration;

namespace Cetus;

internal sealed record LaunchRequest(bool StartInBackground, string? WorkspacePath, string? UpdateHealthPath)
{
    public static readonly LaunchRequest Empty = new(false, null, null);
}

/// <summary>
/// Parses command-line launch arguments. Recognized forms: --background,
/// a directory path (open a session there) and cetus://open?path=... URLs.
/// </summary>
internal static class LaunchArgs
{
    private const string ProtocolScheme = "cetus";

    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        bool startInBackground = false;
        string? workspacePath = null;
        string? updateHealthPath = null;

        foreach (string rawArg in args)
        {
            if (rawArg.Equals("--background", StringComparison.OrdinalIgnoreCase))
            {
                startInBackground = true;
                continue;
            }

            if (rawArg.StartsWith("--update-health=", StringComparison.OrdinalIgnoreCase))
            {
                updateHealthPath ??= ValidateUpdateHealthPath(rawArg["--update-health=".Length..]);
                continue;
            }

            workspacePath ??= ResolveWorkspacePath(rawArg);
        }

        return new LaunchRequest(startInBackground, workspacePath, updateHealthPath);
    }

    internal static string? ValidateUpdateHealthPath(string candidate)
    {
        try
        {
            string fullPath = Path.GetFullPath(candidate);
            string cacheRoot = Path.GetFullPath(CetusPaths.UpdateCacheDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fileName = Path.GetFileName(fullPath);
            bool validName = fileName.StartsWith("update-health-", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".ready", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(
                    fileName["update-health-".Length..^".ready".Length],
                    "N",
                    out _);
            return validName && fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Directory path, or null when the argument is not an existing directory.</summary>
    public static string? ResolveWorkspacePath(string argument)
    {
        string? candidate = argument.StartsWith($"{ProtocolScheme}://", StringComparison.OrdinalIgnoreCase)
            ? ExtractProtocolPath(argument)
            : argument;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string normalized = RecentWorkspaces.NormalizePath(candidate);
        return Directory.Exists(normalized) ? normalized : null;
    }

    /// <summary>Extracts the path from cetus://open?path=... (URL-decoded).</summary>
    public static string? ExtractProtocolPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(ProtocolScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? source = null;
        if (!string.IsNullOrEmpty(uri.Query))
        {
            string query = uri.Query.TrimStart('?');
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=');
                if (separator > 0
                    && pair[..separator].Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    source = Uri.UnescapeDataString(pair[(separator + 1)..]);
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrEmpty(uri.Host))
        {
            // cetus://F:\repo style hosts decode into the Host component.
            source = Uri.UnescapeDataString(uri.Host + uri.AbsolutePath);
        }

        return string.IsNullOrWhiteSpace(source) ? null : source;
    }
}
