using Microsoft.Win32;

namespace Cetus.Platform;

/// <summary>
/// Registers the cetus:// URL protocol under HKCU (per-user, no elevation)
/// so links like cetus://open?path=... launch Cetus. Idempotent: the command
/// value is rewritten only when it points somewhere else. Skipped entirely in
/// DEV mode so development binaries never claim the protocol.
/// </summary>
internal static class CetusProtocolManager
{
    private const string ProtocolKeyPath = @"Software\Classes\cetus";

    public static void EnsureRegistered()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            using RegistryKey protocolKey = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath, writable: true);
            if (protocolKey.GetValue(string.Empty) is not string)
            {
                protocolKey.SetValue(string.Empty, "URL:CETUS 工作区", RegistryValueKind.String);
            }

            if (protocolKey.GetValue("URL Protocol") is not string)
            {
                protocolKey.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
            }

            using RegistryKey commandKey = protocolKey.CreateSubKey(@"shell\open\command", writable: true);
            string desired = BuildCommand(executablePath);
            if (!string.Equals(commandKey.GetValue(string.Empty) as string, desired, StringComparison.OrdinalIgnoreCase))
            {
                commandKey.SetValue(string.Empty, desired, RegistryValueKind.String);
            }
        }
        catch (System.Security.SecurityException)
        {
            // Registry hardening must not break startup.
        }
        catch (System.IO.IOException)
        {
        }
    }

    public static string BuildCommand(string executablePath) =>
        $"\"{executablePath}\" \"%1\"";
}
