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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CetusTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
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
