using System.Diagnostics;
using System.IO;
using System.Windows;
using Cetus.Configuration;
using Cetus.Hosting;
using Cetus.Updates;

namespace Cetus;

/// <summary>
/// Update orchestration on the view side: silent startup checks, the
/// in-app announcement window, the health marker consumed by the update
/// rollback guard, and the settings-page entries.
/// </summary>
public partial class MainWindow
{
    private static void WriteUpdateHealthMarker(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, "ready", new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Append($"Unable to write update health marker: {error.Message}");
        }
    }
    private void ShowPortableUpdateFailureIfPresent()
    {
        string path = PortableUpdateApplier.FailureNoticePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            PortableUpdateFailureNotice? notice = PortableUpdateFailureNotice.TryRead(path);
            string detail = notice?.Reason is { Length: > 0 } reason
                ? reason
                : "新版本启动失败，已恢复旧版本。";
            _tray?.ShowBalloonTip("CETUS 更新失败", detail, null);

            // Remember the failed version so the silent startup updater does not
            // reinstall it and kill DSH again on every launch.
            if (notice?.Version is { Length: > 0 } version
                && Version.TryParse(version.Trim().TrimStart('v', 'V'), out Version? parsed))
            {
                UpdateRejection.Record(parsed);
            }

            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Append($"Unable to read portable update failure notice: {error.Message}");
        }
    }
    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            await _updates!.CheckForUpdatesAsync(interactive: false);
        }
        catch
        {
            // Startup update checks must never surface as errors.
        }
    }
    /// <summary>
    /// Shows the GitHub Pages announcement page in the in-app browser after a
    /// completed update: the running version is higher than the version
    /// recorded at the previous launch. First launches and downgrades stay
    /// quiet; the recorded version is refreshed on every launch.
    /// </summary>
    private void ShowUpdateAnnouncementIfDue()
    {
        if (_announcementShown)
        {
            return;
        }

        _announcementShown = true;
        string current = UpdateCoordinator.ReadCurrentVersion().ToString();
        string? previous = _settings.LastLaunchVersion;
        if (UpdateAnnouncement.ShouldAnnounce(previous, current))
        {
            OpenUpdateAnnouncement(UpdateAnnouncement.BuildPageUrl(previous, current));
        }

        _settings.SetLastLaunchVersion(current);
    }
    private void OpenUpdateAnnouncement(string url)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OpenUpdateAnnouncement(url));
            return;
        }

        try
        {
            new AnnouncementWindow(url, IsSystemDarkMode()) { Owner = this }.Show();
        }
        catch
        {
            OpenInSystemBrowser(url);
        }
    }
    private static void OpenInSystemBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Shell execution failure must not crash the desktop app.
        }
    }
    private async Task CheckForUpdatesFromSettingsAsync()
    {
        if (_isExiting)
        {
            return;
        }

        // Already holding a newer release: show it again instead of re-querying.
        if (EnsureUpdateCoordinator().HasAvailableUpdate)
        {
            EnsureUpdateCoordinator().PostUpdateState();
            return;
        }

        await EnsureUpdateCoordinator().CheckForUpdatesAsync(interactive: true);
    }
    /// <summary>
    /// Answers the settings-page DSH check: compares the bundled runtime
    /// against npm dist-tags and guides the user to the CETUS release page
    /// (the runtime ships inside CETUS releases and is never hot-swapped).
    /// </summary>
    private async Task CheckDshUpdateAsync()
    {
        if (_isExiting)
        {
            return;
        }

        string current = DshRuntimeInfo.ReadInstalledVersion(AppContext.BaseDirectory) ?? "开发构建";
        DshDistTags? tags;
        using (var feed = new NpmDistTagFeed())
        {
            tags = await feed.FetchAsync(CancellationToken.None);
        }

        if (tags is null)
        {
            _ = MessageBox.Show(
                this,
                "无法查询 npm 上游版本，请稍后重试。",
                "CETUS · DSH 版本",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string tagLines = string.Join(
            Environment.NewLine,
            tags.EnumerateParsedTags().Select(tag => $"{tag.Channel} 通道：{tag.Version}"));
        DshDistTag? highest = tags.HighestAvailable;
        if (highest is null)
        {
            _ = MessageBox.Show(
                this,
                "npm 上游没有返回可识别的 DSH 版本，请稍后重试。",
                "CETUS · DSH 版本",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool upToDate = DshVersion.TryParse(current, out DshVersion? installed)
            && installed is not null
            && DshVersion.Compare(installed, highest.ParsedVersion) >= 0;
        if (upToDate)
        {
            _ = MessageBox.Show(
                this,
                $"当前内嵌 DSH {current} 已覆盖 npm 上游可识别的最高版本。{Environment.NewLine}{tagLines}",
                "CETUS · DSH 版本",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult choice = MessageBox.Show(
            this,
            $"发现新的 DSH：{highest.Version}（{highest.Channel} 通道）{Environment.NewLine}{tagLines}{Environment.NewLine}{Environment.NewLine}" +
            $"当前内嵌版本：{current}。DSH 随 CETUS 版本一起发布，请更新 CETUS 本体以获取新 Harness。",
            "CETUS · DSH 版本",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (choice == MessageBoxResult.OK)
        {
            OpenUpdateAnnouncement("https://github.com/AvroraCL/CETUS-Desktop/releases");
        }
    }
}
