namespace Cetus.Desktop.Core;

/// <summary>
/// Cetus runtime configuration. For M0 every value is environment-driven so the
/// spawn path can be tested against a separate port / DSH_HOME without touching
/// the machine-wide installation (later milestone: bundled node + pinned dsh).
/// </summary>
public sealed class CetusConfig
{
    public string Host { get; } = GetEnv("CETUS_HOST", "127.0.0.1");
    public int Port { get; } = ParseInt(GetEnv("CETUS_PORT", "3080"), 3080);
    public string? NodeExe { get; } = GetEnvOrNull("CETUS_NODE_EXE");
    public string? DshCliJs { get; } = GetEnvOrNull("CETUS_DSH_CLI_JS");
    public string? DshHome { get; } = GetEnvOrNull("DSH_HOME");
    public TimeSpan ReadyTimeout { get; } = TimeSpan.FromSeconds(60);
    public TimeSpan HealthTimeout { get; } = TimeSpan.FromSeconds(3);

    public Uri Url => new($"http://{Host}:{Port}/");

    public static CetusConfig Load() => new();

    private static string GetEnv(string name, string fallback) => GetEnvOrNull(name) ?? fallback;

    private static string? GetEnvOrNull(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed is >= 0 and <= 65535 ? parsed : fallback;
}
