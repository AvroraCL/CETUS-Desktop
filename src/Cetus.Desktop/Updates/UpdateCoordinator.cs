using System.Diagnostics;
using System.Net.Http;
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
    /// Silent startup path: installed editions download the installer and
    /// portable editions download the zip bundle — both install without any
    /// prompt. Startup checks stay quiet; the release-notes role moved to
    /// the GitHub Pages announcement page, which the new build opens after
    /// the restart.
    /// </summary>
    private async Task AutoInstallAsync(ReleaseInfo release, UpdateFeedSource source, bool installedEdition)
    {
        if (!installedEdition)
        {
            if (DevModeFlag.IsActive)
            {
                // A DEV binary runs out of the build tree; mirroring a
                // release bundle over it would destroy the checkout. Only
                // real portable installs self-replace.
                _notify(
                    "CETUS 更新",
                    $"发现新版本 {release.TagName}。开发构建不做自动升级，点击查看更新公告。",
                    OpenAnnouncementPage);
                return;
            }

            _notify(
                "CETUS 更新",
                $"发现新版本 {release.TagName}，正在后台下载便携更新包，完成后将自动升级并重启。",
                null);
            SetTaskbarProgress(Indeterminate);
            try
            {
                await ApplyPortableUpdateAsync(null, null, release, source);
            }
            catch (Exception error)
            {
                _notify("CETUS 更新失败", $"便携更新没有完成：{error.Message}", null);
            }
            finally
            {
                SetTaskbarProgress(null);
            }

            return;
        }

        _notify(
            "CETUS 更新",
            $"发现新版本 {release.TagName}，正在后台下载，完成后将自动安装并重启。",
            null);
        SetTaskbarProgress(Indeterminate);
        try
        {
            UpdateFeedSource downloadSource = await PickDownloadSourceAsync(source);
            string installerPath = await _service.DownloadInstallerAsync(
                release,
                downloadSource,
                new Progress<double>(ReportTaskbarProgress),
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
        finally
        {
            SetTaskbarProgress(null);
        }
    }

    /// <summary>
    /// Downloads from whichever feed answers faster right now (large payloads
    /// amplify a slow check). The winner is remembered as the preferred
    /// source; probe failure keeps the source that already answered.
    /// </summary>
    private async Task<UpdateFeedSource> PickDownloadSourceAsync(UpdateFeedSource source)
    {
        try
        {
            UpdateFeedSource? faster = await _service.ProbeFasterSourceAsync(source, CancellationToken.None);
            if (faster is { } picked)
            {
                if (picked != source)
                {
                    _settings.SetUpdateSource(ToSettingValue(picked));
                }

                return picked;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Speed probing is advisory; fall through to the known source.
        }

        return source;
    }

    private static string ToSettingValue(UpdateFeedSource source) => source switch
    {
        UpdateFeedSource.GitCode => "gitcode",
        _ => "github",
    };

    /// <summary>
    /// Applies a portable zip bundle: download into the cache, extract to a
    /// staging folder, then hand control to a takeover script that runs
    /// after CETUS exits. Throws on any failure so the caller can report.
    /// </summary>
    private async Task ApplyPortableUpdateAsync(
        UpdatePromptDialog? prompt,
        CancellationTokenSource? cancellation,
        ReleaseInfo release,
        UpdateFeedSource source)
    {
        UpdateFeedSource downloadSource = await PickDownloadSourceAsync(source);
        string zipPath = await _service.DownloadPortableBundleAsync(
            release,
            downloadSource,
            new Progress<double>(value =>
            {
                prompt?.ReportProgress(value);
                ReportTaskbarProgress(value);
            }),
            cancellation?.Token ?? CancellationToken.None);
        string staging = PortableUpdateApplier.PrepareStaging(zipPath, release.Version);
        string script = PortableUpdateApplier.WriteApplyScript(
            staging, AppContext.BaseDirectory, Environment.ProcessId, zipPath);
        PortableUpdateApplier.LaunchApplyScript(script);
        prompt?.Close();
        _notify("CETUS 更新", "便携更新已就绪，CETUS 即将退出并升级到新版本。", null);
        _exitApplication();
    }

    private const double Indeterminate = -1;

    /// <summary>Taskbar download progress; null clears, negative shows indeterminate, otherwise 0..1.</summary>
    private void SetTaskbarProgress(double? value)
    {
        System.Windows.Shell.TaskbarItemInfo info = _owner.TaskbarItemInfo ??= new();
        if (value is null)
        {
            info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }
        else if (value < 0)
        {
            info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
        }
        else
        {
            info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            info.ProgressValue = Math.Clamp(value.Value, 0, 1);
        }
    }

    private void ReportTaskbarProgress(double value) => SetTaskbarProgress(value);

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
        prompt.InstallClicked += () => _ = RunInstallAsync(prompt, release, source, installedEdition);
        prompt.OpenReleasesClicked += () =>
        {
            OpenReleasesPage();
            prompt.Close();
        };
        prompt.ShowDialog();
    }

    private async Task RunInstallAsync(
        UpdatePromptDialog prompt,
        ReleaseInfo release,
        UpdateFeedSource source,
        bool installedEdition)
    {
        var cancellation = new CancellationTokenSource();
        prompt.CancelClicked += cancellation.Cancel;
        prompt.SetDownloading(true);
        SetTaskbarProgress(Indeterminate);
        var progress = new Progress<double>(value =>
        {
            prompt.ReportProgress(value);
            ReportTaskbarProgress(value);
        });
        try
        {
            if (installedEdition)
            {
                UpdateFeedSource downloadSource = await PickDownloadSourceAsync(source);
                string installerPath = await _service.DownloadInstallerAsync(
                    release,
                    downloadSource,
                    progress,
                    cancellation.Token);
                Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    Arguments = "/SILENT",
                });
                _exitApplication();
            }
            else
            {
                if (DevModeFlag.IsActive)
                {
                    prompt.SetDownloading(false);
                    prompt.ReportStatus("开发构建不做应用内升级，请打开发布页获取正式包。", isError: true);
                    return;
                }

                await ApplyPortableUpdateAsync(prompt, cancellation, release, source);
            }
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
        finally
        {
            SetTaskbarProgress(null);
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
