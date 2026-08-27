using Cetus.Configuration;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class CetusPathsTests
{
    [Fact]
    public void Paths_UseOneOverrideRuleForEveryPerUserArtifact()
    {
        using var overrides = new EnvironmentOverrides(
            ("CETUS_SETTINGS_PATH", @"X:\isolated\settings.json"),
            ("CETUS_WEBVIEW2_USER_DATA", @"X:\isolated\webview"),
            ("CETUS_LOG_DIR", @"X:\isolated\logs"));

        Assert.Equal(@"X:\isolated\settings.json", CetusPaths.SettingsFile);
        Assert.Equal(@"X:\isolated\webview", CetusPaths.WebView2UserDataDirectory);
        Assert.Equal(@"X:\isolated\logs", CetusPaths.LogDirectory);
    }

    [Fact]
    public void Paths_IgnoreWhitespaceOverrides()
    {
        using var overrides = new EnvironmentOverrides(
            ("CETUS_SETTINGS_PATH", " "),
            ("CETUS_WEBVIEW2_USER_DATA", "\t"),
            ("CETUS_LOG_DIR", string.Empty));

        Assert.StartsWith(CetusPaths.UserDataDirectory, CetusPaths.SettingsFile);
        Assert.StartsWith(CetusPaths.UserDataDirectory, CetusPaths.WebView2UserDataDirectory);
        Assert.StartsWith(CetusPaths.UserDataDirectory, CetusPaths.LogDirectory);
    }

    private sealed class EnvironmentOverrides : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = [];

        public EnvironmentOverrides(params (string Name, string? Value)[] values)
        {
            foreach ((string name, string? value) in values)
            {
                _originalValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach ((string name, string? value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
