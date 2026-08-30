using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Cetus.Platform;
using Cetus.Configuration;

namespace Cetus.Updates;

/// <summary>
/// Desktop-side update orchestration: check, prompt, download with progress,
/// hand off to the Inno Setup installer and exit. Interactive checks report
/// "already up to date" or failures. Startup checks stay quiet: when an
/// update is found the coordinator announces it via a tray balloon, then
/// downloads and installs silently — the release-notes role moved to the
/// GitHub Pages announcement page, which the new build opens after restart.
/// </summary>
internal sealed class UpdateCoordinator
{
    private readonly Window _owner;
    private readonly Action _exitApplication;
    private readonly UpdateService _service;
    private readonly CetusSettings _settings;
    private readonly Version _currentVersion;
    private readonly Action<string, string, Action?> _notify;
    private readonly Action<string>? _openAnnouncement;
    private string _releasesPageUrl = UpdateCheckResult.Failed("x").ReleasesPageUrl;

    public UpdateCoordinator(
        Window owner,
        Action exitApplication,
        CetusSettings settings,
        Action<string, string, Action?>? notify = null,
        Action<string>? openAnnouncement = null)
        : this(
            owner,
            exitApplication,
            new UpdateService(),
            settings,
            ReadCurrentVersion(),
            notify,
            openAnnouncement)
    {
    }

    internal UpdateCoordinator(
        Window owner,
        Action exitApplication,
        UpdateService service,
        CetusSettings settings,
        Version currentVersion,
        Action<string, string, Action?>? notify = null,
        Action<string>? openAnnouncement = null)
    {
        _owner = owner;
        _exitApplication = exitApplication;
        _service = service;
        _settings = settings;
        _currentVersion = currentVersion;
        _notify = notify ?? ((_, _, _) => { });
        _openAnnouncement = openAnnouncement;
    }

    public async Task CheckForUpdatesAsync(bool interactive)
    {
        UpdateCheckResult result = await _service.CheckAsync(
            _currentVersion,
            _settings.UpdateSource,
            CancellationToken.None);
        _releasesPageUrl = result.ReleasesPageUrl;

        if (result.UpdateAvailable && result.Release is { } found)
        {
            // Remember the source that answered so later checks try it first.
            _settings.SetUpdateSource(result.Source switch
            {
                UpdateFeedSource.GitCode => "gitcode",
                _ => "github",
            });
            if (!interactive)
            {
                await AutoInstallAsync(found, result.Source, InstalledEdition.IsInstalled());
                return;
            }

            await PresentAsync(found, InstalledEdition.IsInstalled(), result.Source);
            return;
        }

        if (!interactive)
        {
            return;
        }

        ShowInfo(
            result.Error is null ? "当前已是最新版本。" : $"检查更新失败：{result.Error}",
            result.Error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>
    /// Silent startup path: installed editions download and hand off to the
    /// installer without any prompt (the installer relaunches CETUS, which
    /// then shows the announcement page). Portable editions cannot replace
    /// themselves, so they only announce and let the balloon click open the
    /// announcement page.
    /// </summary>
    private async Task AutoInstallAsync(ReleaseInfo release, UpdateFeedSource source, bool installedEdition)
    {
        if (!installedEdition)
        {
            _notify(
                "CETUS 更新",
                $"发现新版本 {release.TagName}。便携版无法自动安装，点击查看更新公告。",
                OpenAnnouncementPage);
            return;
        }

        _notify(
            "CETUS 更新",
            $"发现新版本 {release.TagName}，正在后台下载，完成后将自动安装并重启。",
            null);
        try
        {
            string installerPath = await _service.DownloadInstallerAsync(
                release,
                source,
                progress: null,
                CancellationToken.None);
            _notify("CETUS 更新", "下载完成，正在安装更新，CETUS 即将退出。", null);
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT",
            });
            _exitApplication();
        }
        catch (Exception error)
        {
            _notify("CETUS 更新失败", $"自动更新没有完成：{error.Message}", null);
        }
    }

    private void OpenAnnouncementPage()
    {
        if (_openAnnouncement is { } open)
        {
            open(UpdateAnnouncement.BuildPageUrl(null, _currentVersion.ToString()));
            return;
        }

        OpenReleasesPage();
    }

    private async Task PresentAsync(ReleaseInfo release, bool installedEdition, UpdateFeedSource source)
    {
        var prompt = new UpdatePromptDialog(_currentVersion.ToString(), release, installedEdition)
        {
            Owner = _owner,
        };
        prompt.InstallClicked += () => _ = RunInstallAsync(prompt, release, source);
        prompt.OpenReleasesClicked += () =>
        {
            OpenReleasesPage();
            prompt.Close();
        };
        prompt.ShowDialog();
    }

    private async Task RunInstallAsync(UpdatePromptDialog prompt, ReleaseInfo release, UpdateFeedSource source)
    {
        var cancellation = new CancellationTokenSource();
        prompt.CancelClicked += cancellation.Cancel;
        prompt.SetDownloading(true);
        var progress = new Progress<double>(prompt.ReportProgress);
        try
        {
            string installerPath = await _service.DownloadInstallerAsync(
                release,
                source,
                progress,
                cancellation.Token);
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

    private void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_releasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // External launch is best effort.
        }
    }

    internal static Version ReadCurrentVersion()
    {
        string? raw = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return UpdateFeed.TryParseTag(raw, out Version version) ? version : new Version(0, 0, 0);
    }
}
