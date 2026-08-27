namespace Cetus.Configuration;

/// <summary>
/// Opt-in flag for development launches. CETUS_DEV=1 (any value other than
/// "0") brands the shell as a dev build so it can never be mistaken for an
/// installed release and packaging checks stay off the release identity.
/// </summary>
public static class DevModeFlag
{
    public static bool IsEnabled(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "0", StringComparison.OrdinalIgnoreCase);

    public static bool IsActive => IsEnabled(Environment.GetEnvironmentVariable("CETUS_DEV"));
}
