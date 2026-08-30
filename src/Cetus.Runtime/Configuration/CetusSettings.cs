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
    public const string DefaultUpdateSource = "github";
    public const bool DefaultCloseToTray = true;
    public const string DefaultTerminalShellKey = "pwsh";

    /// <summary>Allowed terminal shells, in fallback order.</summary>
    public static readonly IReadOnlyList<string> TerminalShells = new[] { "pwsh", "powershell", "cmd" };

    private readonly string _settingsPath;
    private int _configuredPort;
    private int _rightSidebarWidth;
    private bool _checkUpdatesOnStartup;
    private string _updateSource = DefaultUpdateSource;
    private bool _closeToTray = DefaultCloseToTray;
    private string _defaultTerminalShell = DefaultTerminalShellKey;
    private string? _lastLaunchVersion;

    public CetusSettings(string settingsPath)
    {
        _settingsPath = settingsPath;
        SettingsSnapshot snapshot = Load(settingsPath);
        _configuredPort = snapshot.Port;
        _rightSidebarWidth = snapshot.RightSidebarWidth;
        _checkUpdatesOnStartup = snapshot.CheckUpdatesOnStartup;
        _updateSource = snapshot.UpdateSource;
        _closeToTray = snapshot.CloseToTray;
        _defaultTerminalShell = snapshot.DefaultTerminalShell;
        _lastLaunchVersion = snapshot.LastLaunchVersion;
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

    /// <summary>Last update source that answered ("github" or "gitcode").</summary>
    public string UpdateSource => _updateSource;

    /// <summary>Whether the window close button minimizes to the tray (true) or exits (false).</summary>
    public bool CloseToTray => _closeToTray;

    /// <summary>Preferred sidebar terminal shell ("pwsh", "powershell" or "cmd").</summary>
    public string DefaultTerminalShell => _defaultTerminalShell;

    /// <summary>Version string recorded at the previous launch, used to detect that CETUS just updated itself.</summary>
    public string? LastLaunchVersion => _lastLaunchVersion;

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

    public void SetCloseToTray(bool enabled)
    {
        _closeToTray = enabled;
        Persist();
    }

    public void SetDefaultTerminalShell(string shell)
    {
        if (!TerminalShells.Contains(shell, StringComparer.Ordinal))
        {
            throw new ArgumentException("终端 Shell 只能是 pwsh、powershell 或 cmd。", nameof(shell));
        }

        if (_defaultTerminalShell == shell)
        {
            return;
        }

        _defaultTerminalShell = shell;
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
                file.CheckUpdatesOnStartup ?? DefaultCheckUpdatesOnStartup,
                file.UpdateSource is { } source && (source == "github" || source == "gitcode")
                    ? source
                    : DefaultUpdateSource,
                file.CloseToTray ?? DefaultCloseToTray,
                NormalizeTerminalShell(file.DefaultTerminalShell),
                string.IsNullOrWhiteSpace(file.LastLaunchVersion) ? null : file.LastLaunchVersion.Trim());
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

    private static string NormalizeTerminalShell(string? shell) =>
        shell is not null && TerminalShells.Contains(shell, StringComparer.Ordinal)
            ? shell
            : DefaultTerminalShellKey;

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
            UpdateSource = _updateSource,
            CloseToTray = _closeToTray,
            DefaultTerminalShell = _defaultTerminalShell,
            LastLaunchVersion = _lastLaunchVersion,
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
        public string? UpdateSource { get; set; }
        public bool? CloseToTray { get; set; }
        public string? DefaultTerminalShell { get; set; }
        public string? LastLaunchVersion { get; set; }
    }

    private sealed record SettingsSnapshot(
        int Port,
        int RightSidebarWidth,
        bool CheckUpdatesOnStartup,
        string UpdateSource,
        bool CloseToTray,
        string DefaultTerminalShell,
        string? LastLaunchVersion)
    {
        public static SettingsSnapshot Default { get; } = new(
            DefaultPort,
            DefaultRightSidebarWidth,
            DefaultCheckUpdatesOnStartup,
            DefaultUpdateSource,
            DefaultCloseToTray,
            DefaultTerminalShellKey,
            null);
    }
}
