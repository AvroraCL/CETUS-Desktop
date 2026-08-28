using System.IO;
using Microsoft.Win32;

namespace Cetus.Platform;

/// <summary>
/// Detects whether this copy of CETUS came from the Inno Setup installer by
/// matching the uninstall registry entry's InstallLocation against the running
/// executable. Portable copies have no such key.
/// </summary>
internal static class InstalledEdition
{
    private const string UninstallKeyName =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\{588C7C05-5114-479B-90D3-0FB5829FB0EF}_is1";

    public static bool IsInstalled()
    {
        try
        {
            if (Registry.GetValue(UninstallKeyName, "InstallLocation", null) is not string installLocation
                || string.IsNullOrWhiteSpace(installLocation)
                || Environment.ProcessPath is not { } executablePath)
            {
                return false;
            }

            string normalizedLocation = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(installLocation.TrimEnd()));
            string normalizedExecutable = Path.GetFullPath(executablePath);
            return normalizedExecutable.StartsWith(
                normalizedLocation + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
