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
    public void Load_LegacyPortOnlyFile_UsesDefaults()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "Port": 4312 }""");

        var settings = new CetusSettings(settingsPath);

        Assert.Equal(4312, settings.ConfiguredPort);
        Assert.True(settings.CheckUpdatesOnStartup);
        Assert.True(settings.CloseToTray);
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
    public void Load_IgnoreInvalidCloseToTray()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """{ "CloseToTray": null }""");

        var settings = new CetusSettings(settingsPath);

        Assert.True(settings.CloseToTray);
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

    [Fact]
    public void LastLaunchVersion_PersistsAcrossLoads()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.Null(settings.LastLaunchVersion);
        settings.SetConfiguredPort(4312);
        settings.SetLastLaunchVersion("0.2.3");

        var reloaded = new CetusSettings(settingsPath);
        Assert.Equal("0.2.3", reloaded.LastLaunchVersion);
        Assert.Equal(4312, reloaded.ConfiguredPort);

        reloaded.SetLastLaunchVersion("0.2.4");
        var third = new CetusSettings(settingsPath);
        Assert.Equal("0.2.4", third.LastLaunchVersion);
    }

    [Fact]
    public void Load_LegacySettingsWithoutLastLaunchVersion_YieldsNull()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "Port": 4312, "LastLaunchVersion": "  " }""");

        var settings = new CetusSettings(settingsPath);

        Assert.Null(settings.LastLaunchVersion);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void TryParsePort_RejectsInvalidValues(string value)
    {
        Assert.False(CetusSettings.TryParsePort(value, out _));
    }

    [Fact]
    public void NotifyOnAgentComplete_PersistsAcrossLoads()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.True(settings.NotifyOnAgentComplete);
        settings.SetNotifyOnAgentComplete(false);

        var reloaded = new CetusSettings(settingsPath);
        Assert.False(reloaded.NotifyOnAgentComplete);
    }

    [Fact]
    public void GlobalHotkeyEnabled_PersistsAcrossLoads()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.True(settings.GlobalHotkeyEnabled);
        settings.SetGlobalHotkeyEnabled(false);

        var reloaded = new CetusSettings(settingsPath);
        Assert.False(reloaded.GlobalHotkeyEnabled);
    }

    [Fact]
    public void Load_LegacyFileWithoutNewSwitches_UsesDefaults()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "Port": 4312 }""");

        var settings = new CetusSettings(settingsPath);

        Assert.True(settings.NotifyOnAgentComplete);
        Assert.True(settings.GlobalHotkeyEnabled);
    }

    [Fact]
    public void WindowPlacement_PersistsAcrossLoads()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Assert.Null(settings.WindowBounds);
        Assert.Null(settings.WindowMaximized);
        settings.SetWindowPlacement("100,-40,1280,860", maximized: true);

        Assert.Equal("100,-40,1280,860", settings.WindowBounds);
        Assert.True(settings.WindowMaximized);

        var reloaded = new CetusSettings(settingsPath);
        Assert.Equal("100,-40,1280,860", reloaded.WindowBounds);
        Assert.True(reloaded.WindowMaximized);

        reloaded.SetWindowPlacement(null, false);
        var cleared = new CetusSettings(settingsPath);
        Assert.Null(cleared.WindowBounds);
        Assert.False(cleared.WindowMaximized);
    }

    [Fact]
    public void Load_LegacyFileWithoutWindowPlacement_YieldsNulls()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "Port": 4312 }""");

        var settings = new CetusSettings(settingsPath);

        Assert.Null(settings.WindowBounds);
        Assert.Null(settings.WindowMaximized);
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

    [Fact]
    public void Persist_ConcurrentSaves_ProduceAValidFile()
    {
        using var directory = new TemporaryDirectory();
        string settingsPath = System.IO.Path.Combine(directory.Path, "settings.json");
        var settings = new CetusSettings(settingsPath);

        Parallel.For(0, 32, index =>
        {
            settings.SetConfiguredPort(20000 + index);
            settings.SetCloseToTray(index % 2 == 0);
        });

        // The file must always parse back and hold one of the written values.
        var reloaded = new CetusSettings(settingsPath);
        Assert.InRange(reloaded.ConfiguredPort, 20000, 20031);
    }

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
