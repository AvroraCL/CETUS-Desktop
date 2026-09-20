using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Cetus.Configuration;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Cetus.Browser;

/// <summary>
/// Owns the complete WebView2 session: environment initialization, trusted
/// origin enforcement, external-link delegation and the Harness theme bridge.
/// </summary>
internal sealed class BrowserSession : IBrowserSession, IUpdateNoticeSink, IDisposable
{
    private const string WindowBridgeSource = "cetus-window";

    private static readonly string WindowBridgeScript = """
        (() => {
          const source = 'cetus-window';

          const report = () => {
            const root = document.documentElement;
            if (!root || !window.chrome || !window.chrome.webview) return;
            const classes = [
              root.className,
              root.getAttribute('data-theme'),
              document.body && document.body.className,
              document.body && document.body.getAttribute('data-theme')
            ].filter(Boolean).join(' ').toLowerCase();
            const scheme = getComputedStyle(root).colorScheme.toLowerCase();
            const dark = /(^|[^a-z])(dark|night)(?=$|[^a-z])/.test(classes)
              || (!/(^|[^a-z])(light|day)(?=$|[^a-z])/.test(classes)
                  && (scheme.includes('dark') || window.matchMedia('(prefers-color-scheme: dark)').matches));
            window.chrome.webview.postMessage({ source, type: 'theme', mode: dark ? 'dark' : 'light' });
          };

          // ── CETUS settings group, injected under DSH 通用设置 ──
          const settingsGroupId = 'cetus-settings-group';
          let cetusSettingsState = {};

          const postCetus = (message) => {
            if (!window.chrome || !window.chrome.webview) return;
            window.chrome.webview.postMessage({ source, ...message });
          };

          const findCetusSettingsDialog = () =>
            document.querySelector('div[role="dialog"][aria-modal="true"]');

          const installCetusSettingsStyle = () => {
            if (document.getElementById('cetus-settings-style')) return;
            const style = document.createElement('style');
            style.id = 'cetus-settings-style';
            style.textContent = `
              #cetus-settings-group { min-width: 0; }
              #cetus-settings-group .cetus-caption {
                font-size: 12px; color: var(--dsw-alias-label-tertiary);
                padding: 20px 0 2px; letter-spacing: 0.02em; }
              #cetus-settings-group .cetus-row {
                display: flex; align-items: center; gap: 8px; padding: 16px 0;
                border-bottom: 1px solid var(--dsw-alias-border-l2); }
              #cetus-settings-group .cetus-row:has(.cetus-switch) { cursor: pointer; }
              #cetus-settings-group .cetus-row:last-child { border-bottom: none; }
              #cetus-settings-group .cetus-rowText {
                flex: 1; display: flex; flex-direction: column; gap: 4px;
                padding-right: 48px; min-width: 0; }
              #cetus-settings-group .cetus-title {
                font-size: 14px; color: var(--dsw-alias-label-primary); }
              #cetus-settings-group .cetus-desc {
                font-size: 12px; color: var(--dsw-alias-label-tertiary); }
              #cetus-settings-group .cetus-switch {
                position: relative; flex: 0 0 auto; width: 40px; height: 20px;
                border-radius: 10px; border: 1px solid var(--dsw-alias-border-l3);
                padding: 0; cursor: pointer;
                background: var(--dsw-alias-bg-module-platform);
                transition: background 0.2s var(--ds-ease-in-out, ease); }
              #cetus-settings-group .cetus-switch[aria-checked="true"] {
                background: var(--dsw-alias-state-business-primary);
                border-color: transparent; }
              #cetus-settings-group .cetus-switch::after {
                content: ''; position: absolute; top: 2px; left: 2px;
                width: 14px; height: 14px; border-radius: 50%; background: #fff;
                box-shadow: 0 1px 2px #0003;
                transition: transform 0.2s var(--ds-ease-in-out, ease); }
              #cetus-settings-group .cetus-switch[aria-checked="true"]::after {
                transform: translateX(20px); }
              #cetus-settings-group .cetus-pill {
                flex: 0 0 auto; height: 36px; border: none; border-radius: 18px;
                padding: 0 14px; font-size: 14px; display: inline-flex;
                align-items: center; gap: 12px;
                color: var(--dsw-alias-label-primary);
                background: var(--dsw-alias-bg-module-platform); cursor: pointer; }
              #cetus-settings-group .cetus-pill:hover {
                background: var(--dsw-alias-interactive-bg-hover); }
              @media (prefers-reduced-motion: reduce) {
                #cetus-settings-group .cetus-switch,
                #cetus-settings-group .cetus-switch::after { transition: none; } }
            `;
            document.head.appendChild(style);
          };

          const cetusRow = (title, desc, control) => {
            const row = document.createElement('div');
            row.className = 'cetus-row';
            const text = document.createElement('div');
            text.className = 'cetus-rowText';
            const titleElement = document.createElement('div');
            titleElement.className = 'cetus-title';
            titleElement.textContent = title;
            text.appendChild(titleElement);
            if (desc) {
              const descElement = document.createElement('div');
              descElement.className = 'cetus-desc';
              descElement.textContent = desc;
              text.appendChild(descElement);
            }
            row.appendChild(text);
            row.appendChild(control);
            return row;
          };

          const cetusSwitch = (key, title) => {
            const sw = document.createElement('button');
            sw.type = 'button';
            sw.className = 'cetus-switch';
            sw.setAttribute('role', 'switch');
            sw.setAttribute('aria-label', title);
            sw.dataset.key = key;
            return sw;
          };

          const bindSwitchRow = (row, sw) => {
            const apply = () => {
              const next = sw.getAttribute('aria-checked') !== 'true';
              sw.setAttribute('aria-checked', String(next));
              cetusSettingsState[sw.dataset.key] = next;
              postCetus({
                type: 'cetus-setting-changed',
                key: sw.dataset.key,
                value: String(next)
              });
            };
            sw.addEventListener('click', apply);
            row.addEventListener('click', (event) => {
              if (event.target !== sw) apply();
            });
          };

          const cetusPill = (id) => {
            const pill = document.createElement('button');
            pill.type = 'button';
            pill.className = 'cetus-pill';
            if (id) pill.id = id;
            return pill;
          };

          const syncCetusSettings = () => {
            const group = document.getElementById(settingsGroupId);
            if (!group) return;
            group.querySelectorAll('.cetus-switch').forEach((sw) => {
              const key = sw.dataset.key;
              sw.setAttribute('aria-checked', String(cetusSettingsState[key] === true || cetusSettingsState[key] === 'true'));
            });
            const portPill = document.getElementById('cetus-setting-port');
            if (portPill) portPill.textContent = String(cetusSettingsState.dshPort || '');
            const dshPill = document.getElementById('cetus-setting-dsh');
            if (dshPill) dshPill.textContent = String(cetusSettingsState.dshVersion || '');

            // The update card can be dismissed while the release stays available,
            // so the settings row keeps a permanent way back to it.
            const updatePill = document.getElementById('cetus-setting-update');
            if (updatePill) {
              const available = updateState && (updateState.available === true || updateState.available === 'true');
              updatePill.textContent = available ? `新版本 ${updateState.version || ''}` : '检查更新…';
              updatePill.dataset.pending = available ? 'true' : 'false';
              updatePill.style.borderColor = available ? 'transparent' : '';
              updatePill.style.color = available ? '#fff' : '';
              updatePill.style.background = available
                ? 'var(--dsw-alias-state-business-primary, #2f6bff)' : '';
            }
          };

          // ── CETUS update notice, rendered inside the Harness page ──
          const updateCardId = 'cetus-update-card';
          let updateState = {};

          const installUpdateStyle = () => {
            if (document.getElementById('cetus-update-style')) return;
            const style = document.createElement('style');
            style.id = 'cetus-update-style';
            style.textContent = `
              #cetus-update-card {
                position: fixed; right: 20px; bottom: 20px; z-index: 2147483000;
                width: min(380px, calc(100vw - 40px));
                box-sizing: border-box; padding: 16px 18px;
                display: flex; flex-direction: column; gap: 10px;
                border-radius: 14px; border: 1px solid var(--dsw-alias-border-l2, #0002);
                background: var(--dsw-alias-bg-module-platform, #fff);
                color: var(--dsw-alias-label-primary, #111);
                box-shadow: 0 12px 32px #0000002e;
                font-size: 13px; line-height: 1.5;
                animation: cetus-update-in 0.22s ease-out; }
              @keyframes cetus-update-in {
                from { opacity: 0; transform: translateY(8px); }
                to { opacity: 1; transform: none; } }
              #cetus-update-card .cetus-update-title {
                font-size: 14px; font-weight: 600; }
              #cetus-update-card .cetus-update-version {
                color: var(--dsw-alias-label-tertiary, #666); }
              #cetus-update-card .cetus-update-notes {
                max-height: 150px; overflow: auto; white-space: pre-wrap;
                color: var(--dsw-alias-label-secondary, #444); }
              #cetus-update-card .cetus-update-bar {
                height: 4px; border-radius: 2px; overflow: hidden;
                background: var(--dsw-alias-border-l3, #0001); }
              #cetus-update-card .cetus-update-bar > i {
                display: block; height: 100%; width: 0%;
                background: var(--dsw-alias-state-business-primary, #2f6bff);
                transition: width 0.2s var(--ds-ease-in-out, ease); }
              #cetus-update-card .cetus-update-actions {
                display: flex; align-items: center; gap: 8px; }
              #cetus-update-card button {
                font: inherit; cursor: pointer; border-radius: 8px;
                border: 1px solid var(--dsw-alias-border-l3, #0002);
                background: transparent; color: inherit; padding: 6px 12px; }
              #cetus-update-card button.cetus-update-primary {
                border-color: transparent; font-weight: 600; color: #fff;
                background: var(--dsw-alias-state-business-primary, #2f6bff); }
              #cetus-update-card button:disabled { opacity: 0.6; cursor: default; }
              #cetus-update-card .cetus-update-close {
                position: absolute; right: 8px; top: 6px; border: none;
                padding: 2px 6px; font-size: 16px; line-height: 1;
                background: transparent; color: var(--dsw-alias-label-tertiary, #888); }
            `;
            document.head.appendChild(style);
          };

          const buildUpdateCard = () => {
            installUpdateStyle();
            const card = document.createElement('div');
            card.id = updateCardId;
            card.setAttribute('role', 'status');

            const close = document.createElement('button');
            close.type = 'button';
            close.className = 'cetus-update-close';
            close.setAttribute('aria-label', '忽略此版本');
            close.textContent = '×';
            close.addEventListener('click', () => {
              updateState = {};
              postCetus({ type: 'cetus-update-dismiss' });
              renderUpdateCard();
            });
            card.appendChild(close);

            const title = document.createElement('div');
            title.className = 'cetus-update-title';
            card.appendChild(title);

            const version = document.createElement('div');
            version.className = 'cetus-update-version';
            card.appendChild(version);

            const notes = document.createElement('div');
            notes.className = 'cetus-update-notes';
            card.appendChild(notes);

            const bar = document.createElement('div');
            bar.className = 'cetus-update-bar';
            const fill = document.createElement('i');
            bar.appendChild(fill);
            card.appendChild(bar);

            const actions = document.createElement('div');
            actions.className = 'cetus-update-actions';
            card.appendChild(actions);

            document.body.appendChild(card);

            const primary = document.createElement('button');
            primary.type = 'button';
            primary.className = 'cetus-update-primary';
            primary.addEventListener('click', () => {
              if (primary.disabled || primary.dataset.mode !== 'install') return;
              primary.disabled = true;
              const ui = updateCard;
              if (ui) {
                ui.title.textContent = '正在准备更新…';
                ui.bar.style.display = 'none';
              }
              postCetus({ type: 'cetus-update-install' });
            });

            const details = document.createElement('button');
            details.type = 'button';
            details.textContent = '查看发布说明';
            details.addEventListener('click', () => postCetus({ type: 'cetus-update-details' }));

            return { card, title, version, notes, bar, fill, actions, primary, details };
          };

          let updateCard = null;

          const renderUpdateCard = () => {
            const state = updateState || {};
            const available = state.available === true || state.available === 'true';
            if (!available || !state.version) {
              if (updateCard) { updateCard.card.remove(); updateCard = null; }
              return;
            }

            if (!document.body) return;
            if (!updateCard) updateCard = buildUpdateCard();
            const ui = updateCard;
            ui.card.style.display = '';
            ui.primary.disabled = false;
            ui.version.textContent = `当前 ${state.current || '—'} → 新版本 ${state.version}`;
            ui.notes.textContent = state.notes || '';
            ui.notes.style.display = state.notes ? '' : 'none';

            const busy = state.installing === true || state.installing === 'true';
            const ready = state.ready === true || state.ready === 'true';
            if (busy) {
              const progress = Number(state.progress);
              const known = Number.isFinite(progress) && progress > 0;
              ui.title.textContent = '正在准备更新…';
              ui.bar.style.display = known ? '' : 'none';
              ui.fill.style.width = known ? `${Math.round(progress * 100)}%` : '0%';
              ui.primary.textContent = known ? `${Math.round(progress * 100)}%` : '下载中…';
              ui.primary.disabled = true;
              ui.primary.dataset.mode = 'busy';
            } else {
              ui.title.textContent = '发现 CETUS 新版本';
              ui.bar.style.display = 'none';
              ui.primary.textContent = ready ? '安装并重启' : '立即更新';
              ui.primary.dataset.mode = 'install';
            }

            ui.actions.replaceChildren(ui.primary, ui.details);
          };

          const installCetusSettings = () => {
            const dialog = findCetusSettingsDialog();
            if (!dialog) return;
            const container = dialog.querySelector('[data-slot="settings.general.item"]');
            if (!container) return;
            const section = container.parentElement;
            if (!section || section.querySelector('#' + settingsGroupId)) return;
            installCetusSettingsStyle();

            const group = document.createElement('div');
            group.id = settingsGroupId;

            const caption = document.createElement('div');
            caption.className = 'cetus-caption';
            caption.textContent = 'CETUS设置';
            group.appendChild(caption);

            const checkRow = cetusRow(
              '启动时检查更新', '启动 CETUS 时自动检测新版本',
              cetusSwitch('checkUpdatesOnStartup', '启动时检查更新'));
            bindSwitchRow(checkRow, checkRow.querySelector('.cetus-switch'));
            group.appendChild(checkRow);

            const checkPill = cetusPill('cetus-setting-update');
            checkPill.textContent = '检查更新…';
            checkPill.addEventListener('click', () => {
              // Bring the card back if it was dismissed, then re-check.
              if (updateState && (updateState.available === true || updateState.available === 'true')) {
                renderUpdateCard();
                return;
              }
              checkPill.textContent = '检查中…';
              postCetus({ type: 'cetus-check-updates' });
            });
            group.appendChild(cetusRow('检查更新', '手动检测 CETUS 新版本', checkPill));

            const notifyRow = cetusRow(
              '任务完成提醒', '会话不在前台时，任务完成弹出托盘通知',
              cetusSwitch('notifyOnAgentComplete', '任务完成提醒'));
            bindSwitchRow(notifyRow, notifyRow.querySelector('.cetus-switch'));
            group.appendChild(notifyRow);

            const hotkeyRow = cetusRow(
              '全局快捷键', 'Ctrl+Alt+Space 随时唤起或隐藏 CETUS',
              cetusSwitch('globalHotkeyEnabled', '全局快捷键'));
            bindSwitchRow(hotkeyRow, hotkeyRow.querySelector('.cetus-switch'));
            group.appendChild(hotkeyRow);

            const autostartRow = cetusRow(
              '开机自启', '登录 Windows 后 CETUS 在后台启动并驻留托盘',
              cetusSwitch('launchOnStartup', '开机自启'));
            bindSwitchRow(autostartRow, autostartRow.querySelector('.cetus-switch'));
            group.appendChild(autostartRow);

            const trayRow = cetusRow(
              '关闭按钮', '开启时点关闭按钮最小化到托盘，关闭则直接退出',
              cetusSwitch('closeToTray', '关闭按钮'));
            bindSwitchRow(trayRow, trayRow.querySelector('.cetus-switch'));
            group.appendChild(trayRow);

            const portPill = cetusPill('cetus-setting-port');
            portPill.addEventListener('click', () => postCetus({ type: 'cetus-open-port-settings' }));
            group.appendChild(cetusRow('DSH 端口', 'DSH 服务监听端口，修改后重启生效', portPill));

            const dshPill = cetusPill('cetus-setting-dsh');
            group.appendChild(cetusRow(
              'DSH 版本', '内嵌 DeepSeek Harness，随 CETUS 更新一起升级', dshPill));

            const dshCheckPill = cetusPill(null);
            dshCheckPill.textContent = '检查…';
            dshCheckPill.addEventListener('click', () => postCetus({ type: 'cetus-check-dsh-update' }));
            group.appendChild(cetusRow('检查 DSH 新版本', '查询 npm 上游是否有更新的 Harness', dshCheckPill));

            section.appendChild(group);
            syncCetusSettings();
            postCetus({ type: 'cetus-settings-request' });
          };

          const install = () => {
            const root = document.documentElement;
            if (!root) return;
            const themeObserver = new MutationObserver(report);
            themeObserver.observe(root, { attributes: true, attributeFilter: ['class', 'data-theme', 'style'] });
            if (document.body) {
              themeObserver.observe(document.body, { attributes: true, attributeFilter: ['class', 'data-theme', 'style'] });
              const layoutObserver = new MutationObserver(() => { installCetusSettings(); });
              layoutObserver.observe(document.body, { childList: true, subtree: true });
            }
            window.chrome.webview.addEventListener('message', (event) => {
              const message = event.data;
              if (!message || message.source !== source) return;
              if (message.type === 'cetus-settings-state') {
                cetusSettingsState = message.values || {};
                syncCetusSettings();
              } else if (message.type === 'cetus-update-state') {
                updateState = message.update || {};
                renderUpdateCard();
                syncCetusSettings();
              }
            });
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', report);
            installCetusSettings();
            renderUpdateCard();
            postCetus({ type: 'cetus-update-state-request' });
            report();
          };
          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', install, { once: true });
          } else {
            install();
          }
        })();
        """;

    private readonly WebView2 _view;
    private readonly Action<bool> _themeChanged;
    private readonly Func<IReadOnlyDictionary<string, string>>? _cetusSettingsProvider;
    private readonly Action<string, string>? _cetusSettingChanged;
    private readonly Action? _openPortSettings;
    private readonly Action? _checkForUpdates;
    private readonly Action? _checkDshUpdate;
    private readonly Action? _installUpdate;
    private readonly Action? _openReleasePage;
    private readonly Action? _dismissUpdate;
    private readonly Func<string?>? _updateStateProvider;
    private LoopbackNavigationPolicy? _navigationPolicy;
    private bool _initialized;
    private bool _disposed;

    public BrowserSession(
        WebView2 view,
        Action<bool> themeChanged,
        Func<IReadOnlyDictionary<string, string>>? cetusSettingsProvider = null,
        Action<string, string>? cetusSettingChanged = null,
        Action? openPortSettings = null,
        Action? checkForUpdates = null,
        Action? checkDshUpdate = null,
        Action? installUpdate = null,
        Action? openReleasePage = null,
        Action? dismissUpdate = null,
        Func<string?>? updateStateProvider = null)
    {
        _view = view;
        _themeChanged = themeChanged;
        _cetusSettingsProvider = cetusSettingsProvider;
        _cetusSettingChanged = cetusSettingChanged;
        _openPortSettings = openPortSettings;
        _checkForUpdates = checkForUpdates;
        _checkDshUpdate = checkDshUpdate;
        _installUpdate = installUpdate;
        _openReleasePage = openReleasePage;
        _dismissUpdate = dismissUpdate;
        _updateStateProvider = updateStateProvider;
    }

    /// <summary>
    /// Same resolution as CetusSettings.DshHomeOverride; read here so the
    /// browser signs its cookie against the same DSH home the probe uses.
    /// </summary>
    private static string? ResolveDshHomeOverride()
    {
        string? value = Environment.GetEnvironmentVariable("CETUS_DSH_HOME");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task NavigateAsync(Uri trustedOrigin, CancellationToken cancellationToken)
    {        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(trustedOrigin);
        _navigationPolicy = new LoopbackNavigationPolicy(trustedOrigin);
        if (!_initialized)
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: CetusPaths.WebView2UserDataDirectory);
            await _view.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();

            CoreWebView2 core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += OnTopLevelNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WindowBridgeScript);
            _initialized = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // The override matters: with CETUS_DSH_HOME set, the probe and the
        // session clients sign against that home, so the browser cookie must
        // come from the same place or the page loads a 401 while the probe
        // happily reports the host as healthy.
        if (Cetus.Hosting.DshAuth.TryGetSessionCookie(trustedOrigin, ResolveDshHomeOverride()) is { } cookie)
        {
            var cookieObj = _view.CoreWebView2.CookieManager.CreateCookie(
                cookie.Name,
                cookie.Value,
                trustedOrigin.Host,
                "/");
            cookieObj.IsHttpOnly = true;
            cookieObj.SameSite = CoreWebView2CookieSameSiteKind.Strict;
            _view.CoreWebView2.CookieManager.AddOrUpdateCookie(cookieObj);
        }

        _view.CoreWebView2.Navigate(trustedOrigin.AbsoluteUri);
        _view.Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        if (!_disposed)
        {
            _view.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Runs JavaScript in the current DSH page (trusted loopback content only).</summary>
    public async Task ExecuteScriptAsync(string script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("浏览器会话尚未初始化。");
        }

        await _view.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>Whether the CoreWebView2 environment exists and the bridge is wired.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Requests renderer suspension for a hidden window to cut memory use.
    /// Silent no-op while visible or when WebView2 refuses (e.g. audio).
    /// </summary>
    public async Task TrySuspendAsync()
    {
        if (!_initialized || _disposed || _view.Visibility == Visibility.Visible)
        {
            return;
        }

        try
        {
            await _view.CoreWebView2.TrySuspendAsync();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            // Suspension is opportunistic; resume-on-show keeps this safe.
        }
    }

    /// <summary>Wakes a suspended renderer (also happens automatically on visibility).</summary>
    public void Resume()
    {
        if (!_initialized || _disposed)
        {
            return;
        }

        try
        {
            _view.CoreWebView2.Resume();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void OnTopLevelNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsTrusted(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenInSystemBrowser(e.Uri);
    }

    private void OnFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsTrusted(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenInSystemBrowser(e.Uri);
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            PostCetusSettingsState();
            PostUpdateState();
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument message = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("source", out JsonElement source)
                && source.GetString() == WindowBridgeSource
                && root.TryGetProperty("type", out JsonElement type))
            {
                if (type.GetString() == "theme"
                    && root.TryGetProperty("mode", out JsonElement mode))
                {
                    _themeChanged(mode.GetString() == "dark");
                }
                else if (type.GetString() == "cetus-settings-request")
                {
                    PostCetusSettingsState();
                }
                else if (type.GetString() == "cetus-setting-changed"
                    && root.TryGetProperty("key", out JsonElement key)
                    && root.TryGetProperty("value", out JsonElement value))
                {
                    if (_cetusSettingChanged is not null)
                    {
                        string settingKey = key.GetString() ?? string.Empty;
                        // The bridge always posts values as strings.
                        _cetusSettingChanged(settingKey, value.ToString());
                        PostCetusSettingsState();
                    }
                }
                else if (type.GetString() == "cetus-open-port-settings")
                {
                    _openPortSettings?.Invoke();
                }
                else if (type.GetString() == "cetus-check-dsh-update")
                {
                    _checkDshUpdate?.Invoke();
                }
                else if (type.GetString() == "cetus-check-updates")
                {
                    _checkForUpdates?.Invoke();
                }
                else if (type.GetString() == "cetus-update-state-request")
                {
                    PostUpdateState();
                }
                else if (type.GetString() == "cetus-update-install")
                {
                    _installUpdate?.Invoke();
                }
                else if (type.GetString() == "cetus-update-details")
                {
                    _openReleasePage?.Invoke();
                }
                else if (type.GetString() == "cetus-update-dismiss")
                {
                    _dismissUpdate?.Invoke();
                }
            }
        }
        catch (JsonException)
        {
            // Ignore messages not emitted by the document-created bridge.
        }
    }

    /// <summary>Pushes the current CETUS settings into the injected settings group.</summary>
    public void PostCetusSettingsState()
    {
        if (!_initialized || _disposed || _view.CoreWebView2 is not { } core)
        {
            return;
        }

        IReadOnlyDictionary<string, string> values = _cetusSettingsProvider?.Invoke()
            ?? new Dictionary<string, string>();
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            source = WindowBridgeSource,
            type = "cetus-settings-state",
            values,
        }));
    }

    /// <summary>
    /// Pushes the current update state so the in-page notice can render. The
    /// payload is produced by the update coordinator, which owns the meaning of
    /// every field; when no coordinator exists or the page is not ready yet the
    /// call is remembered and replayed after the next navigation.
    /// </summary>
    public void PostUpdateState()
    {
        if (_updateStateProvider is null)
        {
            return;
        }

        if (!_initialized || _disposed || _view.CoreWebView2 is not { } core)
        {
            // The page will ask for the state again once it loads.
            return;
        }

        string json = _updateStateProvider() ?? EmptyUpdateState;
        using JsonDocument update = JsonDocument.Parse(json);
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            source = WindowBridgeSource,
            type = "cetus-update-state",
            update = update.RootElement.Clone(),
        }));
    }

    private const string EmptyUpdateState = """{"available":false}""";

    private bool IsTrusted(string uriText)
    {
        return _navigationPolicy?.Allows(uriText) == true;
    }

    private static void OpenInSystemBrowser(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Shell execution failure must not crash the desktop app.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.Visibility = Visibility.Collapsed;
        _view.Dispose();
    }
}
