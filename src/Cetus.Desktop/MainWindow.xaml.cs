using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using Cetus.Browser;
using Cetus.Configuration;
using Cetus.DshStatus;
using Cetus.Hosting;
using Cetus.Maintenance;
using Cetus.Platform;
using Cetus.Runtime;
using Cetus.Updates;
using Microsoft.Win32;

namespace Cetus;

/// <summary>
/// Thin WPF view. Runtime coordination, WebView2 policy and native Windows
/// integration live in their respective modules.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CetusSettings _settings;
    private readonly BrowserSession _browserSession;
    private readonly DesktopRuntime _runtime;
    private readonly RecentWorkspaces _recentWorkspaces;

    private TrayIconController? _tray;
    private WindowComposition? _windowComposition;
    private UpdateCoordinator? _updates;
    private GlobalHotkeyManager? _hotkeys;
    private DshSessionWatcher? _sessionWatcher;
    private DshSessionClient? _dshSessionClient;
    private bool _isExiting;
    private bool _startupStarted;
    private bool _announcementShown;

    /// <summary>
    /// Raised when the splash screen should go away: either the runtime
    /// settled (ready or failed) or the user asked for the window early.
    /// </summary>
    public event EventHandler? SplashDismissRequested;

    public MainWindow()
    {
        _settings = CetusSettings.LoadDefault();
        _recentWorkspaces = new RecentWorkspaces(CetusPaths.RecentWorkspacesFile);
        InitializeComponent();

        _browserSession = new BrowserSession(
            Browser,
            ApplyWindowTheme,
            () => new Dictionary<string, string>
            {
                ["checkUpdatesOnStartup"] = _settings.CheckUpdatesOnStartup ? "true" : "false",
                ["closeToTray"] = _settings.CloseToTray ? "true" : "false",
                ["notifyOnAgentComplete"] = _settings.NotifyOnAgentComplete ? "true" : "false",
                ["globalHotkeyEnabled"] = _settings.GlobalHotkeyEnabled ? "true" : "false",
                // The registry is the source of truth for autostart, so this is
                // re-read on every state post instead of coming from settings.
                ["launchOnStartup"] = AutostartManager.IsEnabled() ? "true" : "false",
                ["dshPort"] = _settings.EffectivePort.ToString(),
                ["dshVersion"] = DshRuntimeInfo.ReadInstalledVersion(AppContext.BaseDirectory) ?? "开发构建",
            },
            OnCetusSettingChanged,
            () => _ = ConfigurePortAsync(),
            () => _ = CheckForUpdatesFromSettingsAsync(),
            () => _ = CheckDshUpdateAsync(),
            () => _ = EnsureUpdateCoordinator().InstallAvailableAsync(),
            () => EnsureUpdateCoordinator().OpenAvailableReleasePage(),
            () => EnsureUpdateCoordinator().DismissNotice(),
            () => _updates?.UpdateStateJson());
        _runtime = new DesktopRuntime(_settings, _browserSession, Dispatcher);
        _runtime.StateChanged += OnRuntimeStateChanged;
        _runtime.PortFallback += OnPortFallback;

        if (DevModeFlag.IsActive)
        {
            Title = "CETUS鲸鱼座 · DEV";
            TitleText.Text = "CETUS DEV";
        }

        ApplyWindowTheme(IsSystemDarkMode());
    }

    /// <summary>
    /// Begins startup while the brand splash is showing; the main window
    /// appears only after the runtime settles. With <paramref name="startInBackground"/>
    /// there is no splash and the window stays hidden (autostart path).
    /// </summary>
    public void StartStartup(bool startInBackground = false, string? updateHealthPath = null)
    {
        if (_startupStarted || _isExiting)
        {
            return;
        }

        _startupStarted = true;
        SetupTray();
        RefreshWorkspaceEntries();
        StartBackgroundMaintenance();
        // Create the native HWND (and the WebView2 host surface) without
        // showing the window — EnsureCoreWebView2Async would otherwise wait
        // forever for a parent handle while the splash is up.
        var interopHelper = new System.Windows.Interop.WindowInteropHelper(this);
        interopHelper.EnsureHandle();
        // Attach composition from the raw HwndSource before anything can
        // present a frame. PresentationSource.FromVisual only registers
        // later, and a post-first-frame attach bakes an opaque surface
        // (solid title bar); the raw source is available immediately.
        if (System.Windows.Interop.HwndSource.FromHwnd(interopHelper.Handle)
            is { } source)
        {
            _windowComposition = WindowComposition.AttachSource(
                source,
                () =>
                {
                    if (!_isExiting)
                    {
                        _tray?.RestoreAfterExplorerRestart();
                    }
                });
            _windowComposition.SetDarkMode(IsSystemDarkMode());

            _hotkeys = new GlobalHotkeyManager(source);
            _hotkeys.ToggleRequested += OnGlobalHotkeyToggle;
            if (_settings.GlobalHotkeyEnabled)
            {
                _hotkeys.Register();
            }
        }

        _ = RunStartupAsync(startInBackground, updateHealthPath);
        if (_settings.CheckUpdatesOnStartup)
        {
            EnsureUpdateCoordinator();
            _ = CheckForUpdatesSilentlyAsync();
        }
    }

    private async Task RunStartupAsync(bool startInBackground = false, string? updateHealthPath = null)
    {
        // Splash phase: bring the DSH host up with the window still hidden —
        // WebView2 cannot initialize on a window that was never shown.
        DesktopRuntimeResult result = await _runtime.StartAsync(navigateWithUi: false);
        if (_isExiting)
        {
            return;
        }

        // Composition was attached right after EnsureHandle above — before
        // any frame can be presented — so nothing races Show() here.
        SplashDismissRequested?.Invoke(this, EventArgs.Empty);
        if (!startInBackground)
        {
            Show();
        }

        ShowRuntimeError(result, "Cetus · 启动失败");
        if (!result.Succeeded)
        {
            return;
        }

        // Visible phase: load the DSH page with the status line showing.
        try
        {
            await _runtime.NavigateHomeAsync();
            WriteUpdateHealthMarker(updateHealthPath);
            ShowPortableUpdateFailureIfPresent();
        }
        catch (Exception error)
        {
            ShowRuntimeError(DesktopRuntimeResult.Failed(error), "Cetus · 启动失败");
            return;
        }

        ShowUpdateAnnouncementIfDue();

        // A workspace activation that arrived while the host was still
        // starting (launch args or early IPC) runs now that the page is up.
        if (ConsumePendingWorkspace() is { } pendingWorkspace)
        {
            _ = OpenWorkspaceAsync(pendingWorkspace);
        }
    }

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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_windowComposition is null)
        {
            _windowComposition = WindowComposition.Attach(
                this,
                () =>
                {
                    if (!_isExiting)
                    {
                        _tray?.RestoreAfterExplorerRestart();
                    }
                });
            _windowComposition.SetDarkMode(IsSystemDarkMode());
        }

        // The splash flow starts the runtime before the window is shown; this
        // only fires on the legacy path where the window appears unprompted.
        if (_startupStarted)
        {
            return;
        }

        SetupTray();
        DesktopRuntimeResult result = await _runtime.StartAsync();
        ShowRuntimeError(result, "Cetus · 启动失败");
        ShowUpdateAnnouncementIfDue();
        if (_settings.CheckUpdatesOnStartup)
        {
            EnsureUpdateCoordinator();
            _ = CheckForUpdatesSilentlyAsync();
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

    private UpdateCoordinator EnsureUpdateCoordinator()
    {
        if (_updates is null)
        {
            _updates = new UpdateCoordinator(
                this,
                ExitApplication,
                _settings,
                notify: (title, message, onClick) => _tray?.ShowBalloonTip(title, message, onClick),
                openAnnouncement: OpenUpdateAnnouncement,
                browser: _browserSession,
                showWindow: ShowWindow);
        }

        return _updates;
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

    private void OnRuntimeStateChanged(object? sender, DesktopRuntimeStateChangedEventArgs e)
    {
        DesktopRuntimeState state = e.State;
        _tray?.SetRetryEnabled(state.CanRetry);

        if (state.Phase == DesktopRuntimePhase.Ready)
        {
            StatusText.Visibility = Visibility.Collapsed;
            DiagnosticsPanel.Visibility = Visibility.Collapsed;
            EnsureSessionWatcher();
            return;
        }

        if (state.Phase == DesktopRuntimePhase.Failed && state.Error is not null)
        {
            ShowDiagnostics(state);
            return;
        }

        DiagnosticsPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(state.Message))
        {
            StatusText.Text = state.Message;
        }
    }


    private void SetupTray()
    {
        if (_tray is not null)
        {
            return;
        }

        UpdateCoordinator updates = EnsureUpdateCoordinator();
        _tray = new TrayIconController(new TrayCommands(
            ShowWindow,
            RetryDshAsync,
            ConfigurePortAsync,
            () => updates.CheckForUpdatesAsync(interactive: true),
            ExitApplication,
            path => _ = OpenWorkspaceAsync(path),
            PickWorkspace,
            () => ExportDiagnosticsAsync()));
        _tray.SetRetryEnabled(_runtime.State.CanRetry);
    }

    private string? PickWorkspace()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
            Description = "选择要用 CETUS 打开的工作区目录",
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    /// <summary>
    /// Restores the persisted window bounds. Fires from EnsureHandle during
    /// startup — before the splash flow shows the window — so the frame
    /// materializes in the right place on the first paint. Bounds are
    /// clamped into the current virtual screen: a detached monitor must not
    /// leave the window stranded where the user cannot reach it.
    /// </summary>
    private void OnMinimizeClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.MaximizeWindow(this);

    private void OnRestoreClicked(object sender, RoutedEventArgs e) =>
        SystemCommands.RestoreWindow(this);

    private void OnCloseToTrayClicked(object sender, RoutedEventArgs e) => Close();

    private void ShowWindow()
    {
        SplashDismissRequested?.Invoke(this, EventArgs.Empty);
        if (_rendererSuspended)
        {
            _browserSession.Resume();
            _rendererSuspended = false;
        }
        _hiddenSince = null;
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// One-minute heartbeat for background maintenance: after five hidden
    /// minutes the WebView2 renderer is suspended to cut memory (it resumes
    /// on visibility), and long-retired logs/update caches get pruned once.
    /// </summary>
    private void EnsureSessionWatcher()
    {
        if (_sessionWatcher is not null || _isExiting)
        {
            return;
        }

        // Endpoint is resolved per poll so a port change is picked up
        // without restarting the watcher. The client is shared with the
        // workspace launcher and owned by MainWindow.
        _dshSessionClient ??= new DshSessionClient(_settings.DshHomeOverride);
        _sessionWatcher = new DshSessionWatcher(
            _dshSessionClient,
            () => _runtime.Endpoint);
        _sessionWatcher.AgentFinished += OnAgentFinished;
        _sessionWatcher.Start();
    }

    private void OnAgentFinished(object? sender, DshAgentFinishedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting || _tray is null || !_settings.NotifyOnAgentComplete)
            {
                return;
            }

            // A user watching the session sees the answer live; only
            // surface the balloon when CETUS is not the focused window.
            if (IsVisible && ForegroundWindow.IsCurrent(this))
            {
                return;
            }

            _tray.ShowBalloonTip(
                "任务完成",
                $"「{e.Title}」已完成回复",
                () => _ = FocusSessionAsync(e.SessionId));
        });
    }

    private void OnGlobalHotkeyToggle()
    {
        if (_isExiting)
        {
            return;
        }

        if (IsVisible && ForegroundWindow.IsCurrent(this))
        {
            Hide();
            ShowInTaskbar = false;
        }
        else
        {
            ShowWindow();
        }
    }


    private async Task RetryDshAsync()
    {
        if (_isExiting)
        {
            return;
        }

        ShowWindow();
        DesktopRuntimeResult result = await _runtime.RetryAsync();
        ShowRuntimeError(result, "Cetus · 启动失败");
    }

    private async Task ConfigurePortAsync()
    {
        if (_runtime.IsBusy || _isExiting)
        {
            return;
        }

        ShowWindow();
        var dialog = new PortSettingsDialog(
            _settings.ConfiguredPort,
            _settings.EffectivePort,
            _settings.IsPortOverridden)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.SelectedPort is not int port)
        {
            return;
        }

        PortChangeResult result = await _runtime.ChangePortAsync(port);
        if (!result.Saved)
        {
            ShowRuntimeError(result.ReconnectResult, "Cetus · 无法保存端口设置");
            return;
        }

        _browserSession.PostCetusSettingsState();

        if (result.IsEnvironmentOverridden)
        {
            _ = MessageBox.Show(
                this,
                "端口设置已保存。当前进程仍受 CETUS_PORT 环境变量覆盖；移除该变量后，保存值将在下次启动时生效。",
                "Cetus · 端口设置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ShowRuntimeError(result.ReconnectResult, "Cetus · 重连失败");
    }

    private void OnCetusSettingChanged(string key, string value)
    {
        switch (key)
        {
            case "checkUpdatesOnStartup":
                _settings.SetCheckUpdatesOnStartup(value == "true");
                break;
            case "closeToTray":
                _settings.SetCloseToTray(value == "true");
                break;
            case "notifyOnAgentComplete":
                _settings.SetNotifyOnAgentComplete(value == "true");
                break;
            case "globalHotkeyEnabled":
                bool hotkeyEnabled = value == "true";
                _settings.SetGlobalHotkeyEnabled(hotkeyEnabled);
                if (hotkeyEnabled)
                {
                    // Registration can fail while another app owns the combo;
                    // the state re-post below keeps the switch truthful.
                    _hotkeys?.Register();
                }
                else
                {
                    _hotkeys?.Unregister();
                }

                break;
            case "launchOnStartup":
                AutostartManager.SetEnabled(value == "true");
                break;
        }
        // The bridge re-posts the settings state right after this callback,
        // and the provider re-reads the registry each time, so rejected
        // hotkeys/autostart writes show up as the original switch state.
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

        string alphaLine = tags?.Alpha is { } alpha && alpha != tags.Latest
            ? $"{Environment.NewLine}alpha 通道：{alpha}"
            : string.Empty;
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

        bool upToDate = Version.TryParse(tags.Latest, out Version? latest)
            && Version.TryParse(current, out Version? installed)
            && installed >= latest;
        if (upToDate || tags.Latest is null)
        {
            _ = MessageBox.Show(
                this,
                $"当前内嵌 DSH {current} 已是 npm 上游最新版本。{alphaLine}",
                "CETUS · DSH 版本",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult choice = MessageBox.Show(
            this,
            $"发现新的 DSH：{tags.Latest}（stable）{alphaLine}{Environment.NewLine}{Environment.NewLine}" +
            $"当前内嵌版本：{current}。DSH 随 CETUS 版本一起发布，请更新 CETUS 本体以获取新 Harness。",
            "CETUS · DSH 版本",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (choice == MessageBoxResult.OK)
        {
            OpenUpdateAnnouncement("https://github.com/AvroraCL/CETUS-Desktop/releases");
        }
    }

    private void ShowRuntimeError(DesktopRuntimeResult result, string title)
    {
        if (result.Error is not { } error)
        {
            return;
        }

        _ = MessageBox.Show(
            this,
            error.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int mode && mode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyWindowTheme(bool isDark)
    {
        _windowComposition?.SetDarkMode(isDark);
        Resources["TitleBarTintBrush"] = CreateBrush(
            isDark ? "#151517" : "#F5F7FA",
            isDark ? 0.70 : 0.78);
        Resources["TitleForegroundBrush"] = CreateBrush(isDark ? "#E0F5F7FA" : "#E6172033");
        Resources["CaptionForegroundBrush"] = CreateBrush(isDark ? "#E0F5F7FA" : "#D6172033");
        Resources["CaptionHoverBrush"] = CreateBrush(isDark ? "#2EFFFFFF" : "#14000000");
        Resources["CaptionPressedBrush"] = CreateBrush(isDark ? "#4AFFFFFF" : "#24000000");
        Resources["CaptionFocusBrush"] = CreateBrush(isDark ? "#24FFFFFF" : "#10000000");
        WindowFrame.Background = CreateBrush(isDark ? "#151517" : "#F8FAFC");
        StatusText.Foreground = CreateBrush(isDark ? "#AAB7CC" : "#52627A");
        DiagnosticsTitle.Foreground = CreateBrush(isDark ? "#F28B82" : "#B4232A");
        DiagnosticsSummary.Foreground = CreateBrush(isDark ? "#93A1BA" : "#52627A");
        DiagnosticsDetail.Background = CreateBrush(isDark ? "#1E2534" : "#F0F3F8");
        DiagnosticsDetail.Foreground = CreateBrush(isDark ? "#C9D4E6" : "#333A45");
        DiagnosticsDetail.BorderBrush = CreateBrush(isDark ? "#2E3A52" : "#D5DCE8");
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            255,
            isDark ? 27 : 245,
            isDark ? 27 : 247,
            isDark ? 28 : 250);
    }

    private static System.Windows.Media.Brush CreateBrush(string color, double opacity = 1) =>
        new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color))
        {
            Opacity = opacity,
        };

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            SaveWindowPlacement();
            if (_settings.CloseToTray)
            {
                Hide();
                ShowInTaskbar = false;
            }
            else
            {
                ExitApplication();
            }
        }
        base.OnClosing(e);
    }

    private void ExitApplication() => _ = ExitApplicationAsync();

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _backgroundHeartbeat?.Stop();
        _backgroundHeartbeat = null;
        SaveWindowPlacement();
        _tray?.Dispose();
        _tray = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        _sessionWatcher?.Dispose();
        _sessionWatcher = null;
        _dshSessionClient?.Dispose();
        _dshSessionClient = null;
        await _runtime.StopAsync();
        _browserSession.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isExiting = true;
        _runtime.StateChanged -= OnRuntimeStateChanged;
        _runtime.PortFallback -= OnPortFallback;
        _tray?.Dispose();
        _tray = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        _sessionWatcher?.Dispose();
        _sessionWatcher = null;
        _dshSessionClient?.Dispose();
        _dshSessionClient = null;
        _windowComposition?.Dispose();
        _windowComposition = null;
        _ = StopAfterUnexpectedCloseAsync();
        base.OnClosed(e);
    }

    private async Task StopAfterUnexpectedCloseAsync()
    {
        try
        {
            await _runtime.StopAsync();
        }
        catch
        {
            // The window is already closing; the sidecar Job Object remains the
            // authoritative cleanup for the DSH process tree.
        }
    }
}
