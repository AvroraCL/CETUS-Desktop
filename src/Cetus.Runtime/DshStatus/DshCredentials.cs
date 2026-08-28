using System.IO;

namespace Cetus.DshStatus;

/// <summary>
/// Reads the DeepSeek API key the same way DSH resolves it: the process
/// environment wins, otherwise the strict YAML mapping in
/// <c>$DSH_HOME/.credentials.yaml</c>. The key never leaves this class except
/// as the return value for the balance request; it is never logged.
/// </summary>
public static class DshCredentials
{
    public const string ApiKeyRef = "DEEPSEEK_API_KEY";

    public static string DefaultBaseUrl
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL");
            return string.IsNullOrWhiteSpace(configured) ? "https://api.deepseek.com" : configured;
        }
    }

    /// <summary>Returns the configured API key, or null when absent.</summary>
    public static string? ReadApiKey(string? dshHome = null)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyRef);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        string path = Path.Combine(ResolveDshHome(dshHome), ".credentials.yaml");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(ApiKeyRef, StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = trimmed.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                return value.Length > 0 ? value : null;
            }
        }
        catch (IOException)
        {
            // A missing or unreadable credentials file means "not configured".
        }

        return null;
    }

    private static string ResolveDshHome(string? overrideHome)
    {
        if (!string.IsNullOrWhiteSpace(overrideHome))
        {
            return overrideHome;
        }

        string? environment = Environment.GetEnvironmentVariable("DSH_HOME");
        return string.IsNullOrWhiteSpace(environment)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : environment;
    }
}
