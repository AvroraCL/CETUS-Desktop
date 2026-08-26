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
    private const string SettingsFileName = "settings.json";

    private readonly string _settingsPath;
    private int _configuredPort;

    public CetusSettings(string settingsPath)
    {
        _settingsPath = settingsPath;
        _configuredPort = LoadConfiguredPort(settingsPath);
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
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cetus", SettingsFileName);
        return new CetusSettings(settingsPath);
    }

    public void SetConfiguredPort(int port)
    {
        if (!TryParsePort(port.ToString(), out _))
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须介于 1 和 65535 之间。");
        }

        _configuredPort = port;
        Persist();
    }

    public static bool TryParsePort(string? value, out int port) =>
        int.TryParse(value, out port) && port is > 0 and <= 65535;

    private static int LoadConfiguredPort(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return DefaultPort;
            }

            SettingsFile? file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(settingsPath));
            return file is not null && TryParsePort(file.Port?.ToString(), out int port)
                ? port
                : DefaultPort;
        }
        catch (IOException)
        {
            return DefaultPort;
        }
        catch (JsonException)
        {
            return DefaultPort;
        }
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
        string json = JsonSerializer.Serialize(new SettingsFile { Port = _configuredPort },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private sealed class SettingsFile
    {
        public int? Port { get; set; }
    }
}
