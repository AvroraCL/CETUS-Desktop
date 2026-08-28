using System.IO;

namespace Cetus.Configuration;

/// <summary>
/// Resolves every per-user Cetus path and its process-level test override in
/// one place. Callers no longer need to know environment variable precedence.
/// </summary>
public static class CetusPaths
{
    public static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cetus");

    public static string SettingsFile => ResolveOverride(
        "CETUS_SETTINGS_PATH",
        Path.Combine(UserDataDirectory, "settings.json"));

    public static string WebView2UserDataDirectory => ResolveOverride(
        "CETUS_WEBVIEW2_USER_DATA",
        Path.Combine(UserDataDirectory, "WebView2"));

    public static string LogDirectory => ResolveOverride(
        "CETUS_LOG_DIR",
        Path.Combine(UserDataDirectory, "logs"));

    public static string UpdateCacheDirectory => ResolveOverride(
        "CETUS_UPDATE_DIR",
        Path.Combine(UserDataDirectory, "updates"));

    private static string ResolveOverride(string variableName, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
