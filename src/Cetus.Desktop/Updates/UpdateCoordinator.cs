using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Cetus.Platform;
using Cetus.Configuration;

namespace Cetus.Updates;

/// <summary>
/// Desktop-side update orchestration: check, prompt, download with progress,
/// hand off to the Inno Setup installer and exit. Interactive checks report
/// "already up to date" or failures; startup checks stay silent unless an
/// update was found.
/// </summary>
internal sealed class UpdateCoordinator
{
    private const string ReleasesUrl = "https://github.com/AvroraCL/CETUS-Desktop/releases/latest";

    private readonly Window _owner;
    private readonly Action _exitApplication;
    private readonly UpdateService _service;
    private readonly Version _currentVersion;

    public UpdateCoordinator(Window owner, Action exitApplication)
        : this(owner, exitApplication, new UpdateService(), ReadCurrentVersion())
    {
    }

    internal UpdateCoordinator(Window owner, Action exitApplication, UpdateService service, Version currentVersion)
    {
        _owner = owner;
        _exitApplication = exitApplication;
        _service = service;
        _currentVersion = currentVersion;
    }

    public async Task CheckForUpdatesAsync(bool interactive)
    {
        UpdateCheckResult result = await _service.CheckAsync(_currentVersion, CancellationToken.None);
        if (!result.UpdateAvailable || result.Release is not { } release)
        {
            if (interactive)
            {
                ShowInfo(
                    result.Error is null ? "当前已是最新版本。" : $"检查更新失败：{result.Error}",
                    result.Error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            return;
        }

        bool installed = InstalledEdition.IsInstalled();
        var prompt = new UpdatePromptDialog(_currentVersion.ToString(), release, installed)
        {
            Owner = _owner,
        };
        prompt.InstallClicked += () => _ = RunInstallAsync(prompt, release);
        prompt.OpenReleasesClicked += () =>
        {
            OpenReleasesPage();
            prompt.Close();
        };
        prompt.ShowDialog();
    }

    private async Task RunInstallAsync(UpdatePromptDialog prompt, ReleaseInfo release)
    {
        var cancellation = new CancellationTokenSource();
        prompt.CancelClicked += cancellation.Cancel;
        prompt.SetDownloading(true);
        var progress = new Progress<double>(prompt.ReportProgress);
        try
        {
            string installerPath = await _service.DownloadInstallerAsync(release, progress, cancellation.Token);
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT",
            });
            _exitApplication();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            prompt.SetDownloading(false);
            prompt.ReportStatus("已取消下载。", isError: false);
        }
        catch (Exception error)
        {
            prompt.SetDownloading(false);
            prompt.ReportStatus($"更新失败：{error.Message}", isError: true);
        }
    }

    private void ShowInfo(string message, MessageBoxImage image) =>
        _ = MessageBox.Show(_owner, message, "CETUS · 更新", MessageBoxButton.OK, image);

    private static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // External launch is best effort.
        }
    }

    private static Version ReadCurrentVersion()
    {
        string? raw = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return UpdateFeed.TryParseTag(raw, out Version version) ? version : new Version(0, 0, 0);
    }
}
