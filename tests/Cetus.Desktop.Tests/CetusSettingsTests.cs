using Cetus.Configuration;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class CetusSettingsTests
{
    [Fact]
    public void SetConfiguredPort_PersistsAcrossLoads()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        string? originalPort = Environment.GetEnvironmentVariable("CETUS_PORT");
        try
        {
            Environment.SetEnvironmentVariable("CETUS_PORT", null);
            var settings = new CetusSettings(settingsPath);

            Assert.Equal(CetusSettings.DefaultPort, settings.ConfiguredPort);
            Assert.Equal(CetusSettings.DefaultRightSidebarWidth, settings.RightSidebarWidth);
            settings.SetConfiguredPort(4312);

            var reloaded = new CetusSettings(settingsPath);
            Assert.Equal(4312, reloaded.ConfiguredPort);
            Assert.Equal(4312, reloaded.EffectivePort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_PORT", originalPort);
        }
    }

    [Fact]
    public void SidebarWidth_PersistsWithoutOverwritingPort()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);
        settings.SetConfiguredPort(4312);

        settings.SetRightSidebarWidth(417.6);

        var reloaded = new CetusSettings(settingsPath);
        Assert.Equal(4312, reloaded.ConfiguredPort);
        Assert.Equal(418, reloaded.RightSidebarWidth);
    }

    [Fact]
    public void Load_LegacyPortOnlyFile_UsesSidebarDefaults()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "Port": 4312 }""");

        var settings = new CetusSettings(settingsPath);

        Assert.Equal(4312, settings.ConfiguredPort);
        Assert.Equal(360, settings.RightSidebarWidth);
        Assert.True(settings.CheckUpdatesOnStartup);
    }

    [Fact]
    public void CheckUpdatesOnStartup_PersistsAcrossLoads()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.True(settings.CheckUpdatesOnStartup);
        settings.SetCheckUpdatesOnStartup(false);

        var reloaded = new CetusSettings(settingsPath);
        Assert.False(reloaded.CheckUpdatesOnStartup);
    }

    [Fact]
    public void UpdateSource_PersistsAndRejectsUnknownValues()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.Equal("github", settings.UpdateSource);
        settings.SetUpdateSource("gitcode");

        var reloaded = new CetusSettings(settingsPath);
        Assert.Equal("gitcode", reloaded.UpdateSource);
        Assert.Throws<ArgumentException>(() => reloaded.SetUpdateSource("example"));
    }

    [Fact]
    public void CloseToTray_PersistsAcrossLoads()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.True(settings.CloseToTray);
        settings.SetCloseToTray(false);

        var reloaded = new CetusSettings(settingsPath);
        Assert.False(reloaded.CloseToTray);
    }

    [Fact]
    public void DefaultTerminalShell_PersistsAndRejectsUnknownValues()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.Equal("pwsh", settings.DefaultTerminalShell);
        settings.SetDefaultTerminalShell("cmd");

        var reloaded = new CetusSettings(settingsPath);
        Assert.Equal("cmd", reloaded.DefaultTerminalShell);
        Assert.Throws<ArgumentException>(() => reloaded.SetDefaultTerminalShell("fish"));
    }

    [Fact]
    public void Load_IgnoreInvalidCloseToTrayAndShell()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """{ "CloseToTray": null, "DefaultTerminalShell": "zsh" }""");

        var settings = new CetusSettings(settingsPath);

        Assert.True(settings.CloseToTray);
        Assert.Equal("pwsh", settings.DefaultTerminalShell);
    }

    [Theory]
    [InlineData(100, 300)]
    [InlineData(300, 300)]
    [InlineData(419.5, 420)]
    [InlineData(520, 520)]
    [InlineData(800, 800)]
    [InlineData(900, 900)]
    [InlineData(2000, 1600)]
    public void SetRightSidebarWidth_ClampsAndRounds(double width, int expected)
    {
        using var directory = new TemporaryDirectory();
        var settings = new CetusSettings(Path.Combine(directory.Path, "settings.json"));

        settings.SetRightSidebarWidth(width);

        Assert.Equal(expected, settings.RightSidebarWidth);
    }

    [Fact]
    public void EffectivePort_UsesCetusPortOnlyForTheCurrentProcess()
    {
        using var directory = new TemporaryDirectory();
        var settings = new CetusSettings(Path.Combine(directory.Path, "settings.json"));
        settings.SetConfiguredPort(4312);

        string? originalPort = Environment.GetEnvironmentVariable("CETUS_PORT");
        try
        {
            Environment.SetEnvironmentVariable("CETUS_PORT", "4313");

            Assert.True(settings.IsPortOverridden);
            Assert.Equal(4313, settings.EffectivePort);
            Assert.Equal(4312, settings.ConfiguredPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_PORT", originalPort);
        }
    }

    [Fact]
    public void LoadDefault_UsesExplicitSettingsPathOverride()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "isolated-settings.json");
        string? originalPath = Environment.GetEnvironmentVariable("CETUS_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CETUS_SETTINGS_PATH", settingsPath);

            CetusSettings settings = CetusSettings.LoadDefault();
            settings.SetConfiguredPort(4312);

            Assert.Equal(4312, settings.ConfiguredPort);
            Assert.True(File.Exists(settingsPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_SETTINGS_PATH", originalPath);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void TryParsePort_RejectsInvalidValues(string value)
    {
        Assert.False(CetusSettings.TryParsePort(value, out _));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void DevModeFlag_ParsesOptInValues(string? value, bool expected) =>
        Assert.Equal(expected, DevModeFlag.IsEnabled(value));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestWorkspace.CreateDirectory();
        }

        public string Path { get; }

        public void Dispose()
        {
            if (TestWorkspace.RetainArtifacts) return;
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Leave failed-test artifacts for diagnosis.
            }
        }
    }
}
