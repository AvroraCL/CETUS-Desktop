using System.IO;
using System.Text.Json;

namespace Cetus.Configuration;

/// <summary>
/// Per-user Cetus settings. The configured port is persisted for this Cetus
/// installation; CETUS_PORT remains a higher-priority, process-only override
/// for automation and isolated test runs.
/// </summary>
public sealed class CetusSettings
{
    public const int DefaultPort = 3080;
    public const int DefaultRightSidebarWidth = 360;
    public const int MinimumRightSidebarWidth = 300;
    public const int MaximumRightSidebarWidth = 1600;
    public const bool DefaultCheckUpdatesOnStartup = true;

    private readonly string _settingsPath;
    private int _configuredPort;
    private int _rightSidebarWidth;
    private bool _checkUpdatesOnStartup;

    public CetusSettings(string settingsPath)
    {
        _settingsPath = settingsPath;
        SettingsSnapshot snapshot = Load(settingsPath);
        _configuredPort = snapshot.Port;
        _rightSidebarWidth = snapshot.RightSidebarWidth;
        _checkUpdatesOnStartup = snapshot.CheckUpdatesOnStartup;
    }

    public int ConfiguredPort => _configuredPort;

    public int EffectivePort
    {
        get
        {
            string? overrideValue = Environment.GetEnvironmentVariable("CETUS_PORT");
            return TryParsePort(overrideValue, out int port) ? port : _configuredPort;
        }
    }

    public bool IsPortOverridden =>
        TryParsePort(Environment.GetEnvironmentVariable("CETUS_PORT"), out _);

    public int RightSidebarWidth => _rightSidebarWidth;

    public bool CheckUpdatesOnStartup => _checkUpdatesOnStartup;

    /// <summary>
    /// Cetus shares the inherited/default DSH_HOME by default. This optional
    /// override is passed only to sidecars started by Cetus, never to a reused
    /// external DSH service.
    /// </summary>
    public string? DshHomeOverride
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("CETUS_DSH_HOME");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public static CetusSettings LoadDefault()
        => new(CetusPaths.SettingsFile);

    public void SetConfiguredPort(int port)
    {
        if (!TryParsePort(port.ToString(), out _))
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须介于 1 和 65535 之间。");
        }

        _configuredPort = port;
        Persist();
    }

    public void SetRightSidebarWidth(double width)
    {
        _rightSidebarWidth = NormalizeRightSidebarWidth(width);
        Persist();
    }

    public void SetCheckUpdatesOnStartup(bool enabled)
    {
        _checkUpdatesOnStartup = enabled;
        Persist();
    }

    public static bool TryParsePort(string? value, out int port) =>
        int.TryParse(value, out port) && port is > 0 and <= 65535;

    private static SettingsSnapshot Load(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return SettingsSnapshot.Default;
            }

            SettingsFile? file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(settingsPath));
            if (file is null)
            {
                return SettingsSnapshot.Default;
            }

            int port = TryParsePort(file.Port?.ToString(), out int configuredPort)
                ? configuredPort
                : DefaultPort;
            return new SettingsSnapshot(
                port,
                file.RightSidebarWidth is { } width
                    ? NormalizeRightSidebarWidth(width)
                    : DefaultRightSidebarWidth,
                file.CheckUpdatesOnStartup ?? DefaultCheckUpdatesOnStartup);
        }
        catch (IOException)
        {
            return SettingsSnapshot.Default;
        }
        catch (JsonException)
        {
            return SettingsSnapshot.Default;
        }
    }

    private static int NormalizeRightSidebarWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return DefaultRightSidebarWidth;
        }

        return Math.Clamp(
            (int)Math.Round(width),
            MinimumRightSidebarWidth,
            MaximumRightSidebarWidth);
    }

    private void Persist()
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("设置文件路径必须包含目录。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = _settingsPath + ".tmp";
        string json = JsonSerializer.Serialize(new SettingsFile
        {
            Port = _configuredPort,
            RightSidebarWidth = _rightSidebarWidth,
            CheckUpdatesOnStartup = _checkUpdatesOnStartup,
        },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private sealed class SettingsFile
    {
        public int? Port { get; set; }
        public int? RightSidebarWidth { get; set; }
        public bool? CheckUpdatesOnStartup { get; set; }
    }

    private sealed record SettingsSnapshot(
        int Port,
        int RightSidebarWidth,
        bool CheckUpdatesOnStartup)
    {
        public static SettingsSnapshot Default { get; } = new(
            DefaultPort,
            DefaultRightSidebarWidth,
            DefaultCheckUpdatesOnStartup);
    }
}
