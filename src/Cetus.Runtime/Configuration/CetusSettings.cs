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
    private static readonly JsonSerializerOptions CachedIndented = new() { WriteIndented = true };
    public const int DefaultPort = 3080;
    public const bool DefaultCheckUpdatesOnStartup = true;
    public const string DefaultUpdateSource = "github";
    public const bool DefaultCloseToTray = true;
    public const bool DefaultNotifyOnAgentComplete = true;
    public const bool DefaultGlobalHotkeyEnabled = true;

    private readonly string _settingsPath;
    private readonly object _persistGate = new();
    private int _configuredPort;
    private bool _checkUpdatesOnStartup;
    private string _updateSource = DefaultUpdateSource;
    private bool _closeToTray = DefaultCloseToTray;
    private bool _notifyOnAgentComplete = DefaultNotifyOnAgentComplete;
    private bool _globalHotkeyEnabled = DefaultGlobalHotkeyEnabled;
    private string? _lastLaunchVersion;
    private string? _windowBounds;
    private bool? _windowMaximized;

    public CetusSettings(string settingsPath)
    {
        _settingsPath = settingsPath;
        SettingsSnapshot snapshot = Load(settingsPath);
        _configuredPort = snapshot.Port;
        _checkUpdatesOnStartup = snapshot.CheckUpdatesOnStartup;
        _updateSource = snapshot.UpdateSource;
        _closeToTray = snapshot.CloseToTray;
        _notifyOnAgentComplete = snapshot.NotifyOnAgentComplete;
        _globalHotkeyEnabled = snapshot.GlobalHotkeyEnabled;
        _lastLaunchVersion = snapshot.LastLaunchVersion;
        _windowBounds = snapshot.WindowBounds;
        _windowMaximized = snapshot.WindowMaximized;
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

    public bool CheckUpdatesOnStartup => _checkUpdatesOnStartup;

    /// <summary>Last update source that answered ("github" or "gitcode").</summary>
    public string UpdateSource => _updateSource;

    /// <summary>Whether the window close button minimizes to the tray (true) or exits (false).</summary>
    public bool CloseToTray => _closeToTray;

    /// <summary>Whether finishing agent sessions surface a tray notification while CETUS is not focused.</summary>
    public bool NotifyOnAgentComplete => _notifyOnAgentComplete;

    /// <summary>Whether the process-wide Ctrl+Alt+Space summon hotkey is registered.</summary>
    public bool GlobalHotkeyEnabled => _globalHotkeyEnabled;

    /// <summary>Version string recorded at the previous launch, used to detect that CETUS just updated itself.</summary>
    public string? LastLaunchVersion => _lastLaunchVersion;

    /// <summary>Normal-state window bounds as "left,top,width,height"; null until the first save.</summary>
    public string? WindowBounds => _windowBounds;

    /// <summary>Whether the window was maximized when it was last saved.</summary>
    public bool? WindowMaximized => _windowMaximized;

    public void SetWindowPlacement(string? bounds, bool maximized)
    {
        string? normalized = string.IsNullOrWhiteSpace(bounds) ? null : bounds.Trim();
        if (_windowBounds == normalized && _windowMaximized == maximized)
        {
            return;
        }

        _windowBounds = normalized;
        _windowMaximized = maximized;
        Persist();
    }

    public void SetLastLaunchVersion(string? version)
    {
        if (_lastLaunchVersion == version)
        {
            return;
        }

        _lastLaunchVersion = version;
        Persist();
    }

    public void SetUpdateSource(string source)
    {
        if (source is not ("github" or "gitcode"))
        {
            throw new ArgumentException("更新源只能是 github 或 gitcode。", nameof(source));
        }

        if (_updateSource == source)
        {
            return;
        }

        _updateSource = source;
        Persist();
    }

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

        if (_configuredPort == port)
        {
            return;
        }

        _configuredPort = port;
        Persist();
    }

    public void SetCheckUpdatesOnStartup(bool enabled)
    {
        if (_checkUpdatesOnStartup == enabled)
        {
            return;
        }

        _checkUpdatesOnStartup = enabled;
        Persist();
    }

    public void SetCloseToTray(bool enabled)
    {
        if (_closeToTray == enabled)
        {
            return;
        }

        _closeToTray = enabled;
        Persist();
    }

    public void SetNotifyOnAgentComplete(bool enabled)
    {
        if (_notifyOnAgentComplete == enabled)
        {
            return;
        }

        _notifyOnAgentComplete = enabled;
        Persist();
    }

    public void SetGlobalHotkeyEnabled(bool enabled)
    {
        if (_globalHotkeyEnabled == enabled)
        {
            return;
        }

        _globalHotkeyEnabled = enabled;
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
                file.CheckUpdatesOnStartup ?? DefaultCheckUpdatesOnStartup,
                file.UpdateSource is { } source && (source == "github" || source == "gitcode")
                    ? source
                    : DefaultUpdateSource,
                file.CloseToTray ?? DefaultCloseToTray,
                file.NotifyOnAgentComplete ?? DefaultNotifyOnAgentComplete,
                file.GlobalHotkeyEnabled ?? DefaultGlobalHotkeyEnabled,
                string.IsNullOrWhiteSpace(file.LastLaunchVersion) ? null : file.LastLaunchVersion.Trim(),
                string.IsNullOrWhiteSpace(file.WindowBounds) ? null : file.WindowBounds.Trim(),
                file.WindowMaximized);
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

    private void Persist()
    {
        // UI and background paths (port self-heal) can race; serialize the
        // temp-file swap so concurrent saves cannot interleave.
        lock (_persistGate)
        {
            PersistCore();
        }
    }

    private void PersistCore()
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("设置文件路径必须包含目录。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = _settingsPath + ".tmp" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(new SettingsFile
        {
            Port = _configuredPort,
            CheckUpdatesOnStartup = _checkUpdatesOnStartup,
            UpdateSource = _updateSource,
            CloseToTray = _closeToTray,
            NotifyOnAgentComplete = _notifyOnAgentComplete,
            GlobalHotkeyEnabled = _globalHotkeyEnabled,
            LastLaunchVersion = _lastLaunchVersion,
            WindowBounds = _windowBounds,
            WindowMaximized = _windowMaximized,
        },
            CachedIndented);
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private sealed class SettingsFile
    {
        public int? Port { get; set; }
        public bool? CheckUpdatesOnStartup { get; set; }
        public string? UpdateSource { get; set; }
        public bool? CloseToTray { get; set; }
        public bool? NotifyOnAgentComplete { get; set; }
        public bool? GlobalHotkeyEnabled { get; set; }
        public string? LastLaunchVersion { get; set; }
        public string? WindowBounds { get; set; }
        public bool? WindowMaximized { get; set; }
    }

    private sealed record SettingsSnapshot(
        int Port,
        bool CheckUpdatesOnStartup,
        string UpdateSource,
        bool CloseToTray,
        bool NotifyOnAgentComplete,
        bool GlobalHotkeyEnabled,
        string? LastLaunchVersion,
        string? WindowBounds,
        bool? WindowMaximized)
    {
        public static SettingsSnapshot Default { get; } = new(
            DefaultPort,
            DefaultCheckUpdatesOnStartup,
            DefaultUpdateSource,
            DefaultCloseToTray,
            DefaultNotifyOnAgentComplete,
            DefaultGlobalHotkeyEnabled,
            null,
            null,
            null);
    }
}
