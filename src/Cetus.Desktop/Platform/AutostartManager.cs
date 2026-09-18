using Microsoft.Win32;

namespace Cetus.Platform;

/// <summary>
/// HKCU Run-key autostart. The registry is the single source of truth — the
/// settings surface reads it fresh on every render, so keys removed manually
/// (or stale entries pointing at a moved executable) stay truthful.
/// </summary>
internal static class AutostartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cetus";
    private const string BackgroundArgument = "--background";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && PointsAtCurrentExecutable(value);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        string? executablePath = Environment.ProcessPath;
        if (enabled && string.IsNullOrWhiteSpace(executablePath))
        {
            // Without a process path there is nothing valid to register; leave
            // the registry untouched so the rendered state stays truthful.
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, BuildRunValue(executablePath!), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static string BuildRunValue(string executablePath) =>
        $"\"{executablePath}\" {BackgroundArgument}";

    internal static string? ExtractExecutablePath(string runValue)
    {
        string trimmed = runValue.TrimStart();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            int closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : null;
        }

        int separator = trimmed.IndexOfAny([' ', '\t']);
        return separator > 0 ? trimmed[..separator] : trimmed;
    }

    private static bool PointsAtCurrentExecutable(string runValue)
    {
        string? executablePath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(executablePath)
            && string.Equals(
                ExtractExecutablePath(runValue),
                executablePath,
                StringComparison.OrdinalIgnoreCase);
    }
}
