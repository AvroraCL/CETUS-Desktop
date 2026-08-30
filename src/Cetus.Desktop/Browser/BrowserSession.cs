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
internal sealed class BrowserSession : IBrowserSession, IDisposable
{
    private const string WindowBridgeSource = "cetus-window";

    private static readonly string WindowBridgeScript = """
        (() => {
          const source = 'cetus-window';
          const topBarId = 'cetus-dsh-topbar';
          let rightSidebarOpen = true;
          let modalOpen = false;
          let modalScheduled = false;

          // Real modals (API key onboarding, settings dialogs...) render
          // [role="dialog"][aria-modal="true"] over a blurred mask; non-modal
          // popovers carry role=dialog without aria-modal and must not match.
          const modalSelector = '[role="dialog"][aria-modal="true"]';
          const reportModal = () => {
            if (modalScheduled) return;
            modalScheduled = true;
            window.setTimeout(() => {
              modalScheduled = false;
              const open = document.querySelector(modalSelector) !== null;
              if (open !== modalOpen) {
                modalOpen = open;
                if (window.chrome && window.chrome.webview) {
                  window.chrome.webview.postMessage({ source, type: 'modal', open });
                }
              }
            }, 60);
          };

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

          // ── current DSH session selection ──
          // DSH persists the selected session as plain JSON in
          // localStorage["dsh.sessions.current"] ({} when cleared); there is
          // no RPC for it, so the bridge intercepts storage writes instead of
          // polling. The wrapper installs before the DSH bundle runs.
          const selectionKey = 'dsh.sessions.current';
          let lastSelectionRaw;
          const extractSelectionId = (raw) => {
            if (raw === null) return null;
            try {
              const parsed = JSON.parse(raw);
              if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
              const id = parsed.sessionId;
              return typeof id === 'string' && id.length > 0 ? id : null;
            } catch {
              return null;
            }
          };
          const publishSelection = (reason) => {
            if (!window.chrome || !window.chrome.webview) return;
            let raw = null;
            try {
              raw = window.localStorage.getItem(selectionKey);
            } catch {
              return;
            }
            if (raw === lastSelectionRaw) return;
            lastSelectionRaw = raw;
            window.chrome.webview.postMessage({
              source,
              type: 'dsh-session-selection',
              reason,
              sessionId: extractSelectionId(raw)
            });
          };
          try {
            for (const name of ['setItem', 'removeItem', 'clear']) {
              const original = Storage.prototype[name];
              Storage.prototype[name] = function (...args) {
                const result = original.apply(this, args);
                if (this === window.localStorage) {
                  queueMicrotask(() => publishSelection(name));
                }
                return result;
              };
            }
            window.addEventListener('storage', (event) => {
              if (event.storageArea === window.localStorage) publishSelection('storage');
            });
          } catch {
            // Storage interception is best effort; panels fall back to heuristics.
          }
          publishSelection('initial');

          const updateRightSidebarButton = () => {
            const button = document.getElementById('cetus-right-sidebar-toggle');
            if (!button) return;
            const label = rightSidebarOpen ? '关闭右侧栏' : '打开右侧栏';
            button.setAttribute('aria-label', label);
            button.setAttribute('aria-pressed', String(rightSidebarOpen));
            button.title = label;
          };

          const findCenterColumn = () => Array.from(
            document.querySelectorAll('div[class*="_centerCol"]')
          ).find((element) => {
            const parent = element.parentElement;
            return parent && getComputedStyle(parent).display === 'grid';
          });

          const installTopBar = () => {
            if (document.getElementById(topBarId)) return;
            const centerColumn = findCenterColumn();
            if (!centerColumn) return;

            if (!document.getElementById('cetus-dsh-topbar-style')) {
              const style = document.createElement('style');
              style.id = 'cetus-dsh-topbar-style';
              style.textContent = `
              #${topBarId} {
                box-sizing: border-box;
                display: flex;
                flex: 0 0 60px;
                align-items: center;
                justify-content: flex-end;
                min-width: 0;
                height: 60px;
                padding: 16px 12px;
                background: var(--dsw-alias-bg-base, transparent);
                border-bottom: 1px solid var(--dsw-alias-border-l1, transparent);
              }
              #cetus-right-sidebar-toggle {
                box-sizing: border-box;
                display: inline-flex;
                flex: 0 0 auto;
                align-items: center;
                justify-content: center;
                width: 28px;
                height: 28px;
                margin: 0;
                padding: 0;
                border: none;
                border-radius: 50%;
                color: var(--dsw-alias-label-secondary, currentColor);
                background: transparent;
                cursor: pointer;
              }
              #cetus-right-sidebar-toggle:hover {
                background: var(--dsw-alias-interactive-bg-hover, transparent);
              }
              #cetus-right-sidebar-toggle:focus-visible {
                outline: 2px solid var(--dsw-alias-state-business-primary, currentColor);
                outline-offset: 1px;
              }
              #cetus-right-sidebar-toggle svg {
                width: 16px;
                height: 16px;
              }
              @media (prefers-reduced-motion: reduce) {
                #${topBarId} { transition: none; }
              }
              `;
              document.head.appendChild(style);
            }

            const topBar = document.createElement('div');
            topBar.id = topBarId;
            topBar.setAttribute('role', 'toolbar');
            topBar.setAttribute('aria-label', 'CETUS 工具栏');

            const button = document.createElement('button');
            button.id = 'cetus-right-sidebar-toggle';
            button.type = 'button';
            button.innerHTML = `
              <svg width="16" height="16" viewBox="0 0 16 16"
                   fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
                <g transform="translate(16 0) scale(-1 1)">
                  <path fill-rule="evenodd" clip-rule="evenodd"
                        d="M9.67272 0.522841C10.8339 0.522841 11.76 0.522714 12.4963 0.602493C13.2453 0.683657 13.8789 0.854248 14.4264 1.25197C14.7504 1.48739 15.0355 1.77247 15.2709 2.0965C15.6686 2.64394 15.8392 3.27758 15.9204 4.02655C16.0002 4.7629 16 5.68895 16 6.85014V9.14986C16 10.3111 16.0002 11.2371 15.9204 11.9735C15.8392 12.7224 15.6686 13.3561 15.2709 13.9035C15.0355 14.2275 14.7504 14.5126 14.4264 14.748C13.8789 15.1458 13.2453 15.3163 12.4963 15.3975C11.76 15.4773 10.8339 15.4772 9.67272 15.4772H6.3273C5.16611 15.4772 4.24006 15.4773 3.50371 15.3975C2.75474 15.3163 2.1211 15.1458 1.57366 14.748C1.24963 14.5126 0.964549 14.2275 0.729131 13.9035C0.331407 13.3561 0.160817 12.7224 0.0796529 11.9735C-0.000126137 11.2371 1.25338e-09 10.3111 1.25338e-09 9.14986V6.85014C1.25329e-09 5.68895 -0.000126137 4.7629 0.0796529 4.02655C0.160817 3.27758 0.331407 2.64394 0.729131 2.0965C0.964549 1.77247 1.24963 1.48739 1.57366 1.25197C2.1211 0.854248 2.75474 0.683657 3.50371 0.602493C4.24006 0.522714 5.16611 0.522841 6.3273 0.522841H9.67272ZM5.54303 1.88715V14.1118C5.78636 14.1128 6.04709 14.1169 6.3273 14.1169H9.67272C10.8639 14.1169 11.7032 14.1164 12.3493 14.0465C12.9824 13.9779 13.3497 13.8494 13.6268 13.6482C13.8354 13.4966 14.0195 13.3125 14.1711 13.1039C14.3723 12.8268 14.5007 12.4595 14.5693 11.8264C14.6393 11.1803 14.6398 10.341 14.6398 9.14986V6.85014C14.6398 5.65896 14.6393 4.81967 14.5693 4.1736C14.5007 3.54048 14.3723 3.17318 14.1711 2.89609C14.0195 2.68747 13.8354 2.50337 13.6268 2.35179C13.3497 2.1506 12.9824 2.02212 12.3493 1.95353C11.7032 1.88358 10.8639 1.88307 9.67272 1.88307H6.3273C6.04709 1.88307 5.78636 1.8862 5.54303 1.88715ZM4.1828 1.91166C3.99125 1.9216 3.8148 1.93577 3.65076 1.95353C3.01764 2.02212 2.65034 2.1506 2.37325 2.35179C2.16463 2.50337 1.98052 2.68747 1.82895 2.89609C1.62776 3.17318 1.49928 3.54048 1.43069 4.1736C1.36074 4.81967 1.36023 5.65896 1.36023 6.85014V9.14986C1.36023 10.341 1.36074 11.1803 1.43069 11.8264C1.49928 12.4595 1.62776 12.8268 1.82895 13.1039C1.98052 13.3125 2.16463 13.4966 2.37325 13.6482C2.65034 13.8494 3.01764 13.9779 3.65076 14.0465C3.81478 14.0642 3.99127 14.0774 4.1828 14.0873V1.91166Z"
                        fill="currentColor" />
                </g>
              </svg>
            `;
            button.addEventListener('click', () => {
              if (!window.chrome || !window.chrome.webview) return;
              window.chrome.webview.postMessage({ source, type: 'right-sidebar-toggle' });
            });
            topBar.appendChild(button);
            centerColumn.prepend(topBar);
            updateRightSidebarButton();
          };

          // ── CETUS settings group, injected under DSH 通用设置 ──
          const settingsGroupId = 'cetus-settings-group';
          let cetusSettingsState = {};
          const shellLabels = { pwsh: 'PowerShell 7', powershell: 'PowerShell 5.1', cmd: 'cmd' };
          const shellOrder = ['pwsh', 'powershell', 'cmd'];

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
            const shellPill = document.getElementById('cetus-setting-shell');
            if (shellPill) shellPill.textContent = shellLabels[cetusSettingsState.defaultTerminalShell] || 'PowerShell 7';
            const portPill = document.getElementById('cetus-setting-port');
            if (portPill) portPill.textContent = String(cetusSettingsState.dshPort || '');
          };

          const installCetusSettings = () => {
            const dialog = findCetusSettingsDialog();
            if (!dialog) return;
            // The general section is showing exactly when its row wrappers
            // exist; each row gets its own wrapper, so inject after the last
            // one (into the section element itself).
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

            const checkPill = cetusPill(null);
            checkPill.textContent = '检查更新…';
            checkPill.addEventListener('click', () => postCetus({ type: 'cetus-check-updates' }));
            group.appendChild(cetusRow('检查更新', '手动检测 CETUS 新版本', checkPill));

            const trayRow = cetusRow(
              '关闭按钮', '开启时点关闭按钮最小化到托盘，关闭则直接退出',
              cetusSwitch('closeToTray', '关闭按钮'));
            bindSwitchRow(trayRow, trayRow.querySelector('.cetus-switch'));
            group.appendChild(trayRow);

            const shellPill = cetusPill('cetus-setting-shell');
            shellPill.addEventListener('click', () => {
              const current = shellOrder.indexOf(cetusSettingsState.defaultTerminalShell);
              const next = shellOrder[(current + 1 + shellOrder.length) % shellOrder.length];
              cetusSettingsState.defaultTerminalShell = next;
              shellPill.textContent = shellLabels[next];
              postCetus({ type: 'cetus-setting-changed', key: 'defaultTerminalShell', value: next });
            });
            group.appendChild(cetusRow(
              '终端默认 Shell', '侧栏终端使用的命令行，缺失时自动回退；点击切换',
              shellPill));

            const portPill = cetusPill('cetus-setting-port');
            portPill.addEventListener('click', () => postCetus({ type: 'cetus-open-port-settings' }));
            group.appendChild(cetusRow('DSH 端口', 'DSH 服务监听端口，修改后重启生效', portPill));

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
              const layoutObserver = new MutationObserver(() => { installTopBar(); installCetusSettings(); });
              layoutObserver.observe(document.body, { childList: true, subtree: true });
              const modalObserver = new MutationObserver(reportModal);
              modalObserver.observe(document.body, { childList: true, subtree: true });
            }
            window.chrome.webview.addEventListener('message', (event) => {
              const message = event.data;
              if (message && message.source === source && message.type === 'right-sidebar-state') {
                rightSidebarOpen = Boolean(message.open);
                updateRightSidebarButton();
              }
              if (message && message.source === source && message.type === 'cetus-settings-state') {
                cetusSettingsState = message.values || {};
                syncCetusSettings();
              }
            });
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', report);
            installTopBar();
            installCetusSettings();
            report();
            reportModal();
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
    private readonly Action _rightSidebarToggleRequested;
    private readonly Action<bool> _dshModalChanged;
    private readonly Func<IReadOnlyDictionary<string, string>>? _cetusSettingsProvider;
    private readonly Action<string, string>? _cetusSettingChanged;
    private readonly Action? _openPortSettings;
    private readonly Action? _checkForUpdates;
    private readonly Action<string?>? _currentSessionChanged;
    private LoopbackNavigationPolicy? _navigationPolicy;
    private bool _rightSidebarOpen = true;
    private bool _initialized;
    private bool _disposed;

    public BrowserSession(
        WebView2 view,
        Action<bool> themeChanged,
        Action rightSidebarToggleRequested,
        Action<bool> dshModalChanged,
        Func<IReadOnlyDictionary<string, string>>? cetusSettingsProvider = null,
        Action<string, string>? cetusSettingChanged = null,
        Action? openPortSettings = null,
        Action? checkForUpdates = null,
        Action<string?>? currentSessionChanged = null)
    {
        _view = view;
        _themeChanged = themeChanged;
        _rightSidebarToggleRequested = rightSidebarToggleRequested;
        _dshModalChanged = dshModalChanged;
        _cetusSettingsProvider = cetusSettingsProvider;
        _cetusSettingChanged = cetusSettingChanged;
        _openPortSettings = openPortSettings;
        _checkForUpdates = checkForUpdates;
        _currentSessionChanged = currentSessionChanged;
    }

    public async Task NavigateAsync(Uri trustedOrigin, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        _view.CoreWebView2.Navigate(trustedOrigin.AbsoluteUri);
        _view.Visibility = Visibility.Visible;
    }

    public void SetRightSidebarOpen(bool isOpen)
    {
        _rightSidebarOpen = isOpen;
        PostRightSidebarState();
    }

    public void Hide()
    {
        if (!_disposed)
        {
            _view.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Inserts text into the DSH chat composer without sending it. Returns
    /// false when the page (or its composer) is not available yet; the caller
    /// should fall back to the clipboard in that case.
    /// </summary>
    public async Task<bool> TryInsertIntoChatAsync(string text)
    {
        if (!_initialized || _disposed || _view.CoreWebView2 is not { } core)
        {
            return false;
        }

        string encoded = System.Web.HttpUtility.JavaScriptStringEncode(text);
        const string script = """
            (() => {
              const composer = document.querySelector('textarea')
                || Array.from(document.querySelectorAll('[contenteditable="true"]'))
                    .find((el) => !el.getAttribute('aria-hidden'));
              if (!composer) return 'false';
              const text = "__CETUS_TEXT__";
              if (composer instanceof HTMLTextAreaElement) {
                const setter = Object.getOwnPropertyDescriptor(
                  window.HTMLTextAreaElement.prototype, 'value').set;
                setter.call(composer, composer.value ? composer.value + '\n\n' + text : text);
                composer.dispatchEvent(new Event('input', { bubbles: true }));
              } else {
                composer.focus();
                document.execCommand('insertText', false, text);
                composer.dispatchEvent(new Event('input', { bubbles: true }));
              }
              composer.focus();
              return 'true';
            })()
            """;
        // ExecuteScriptAsync returns the JSON-encoded value, so a JS 'true'
        // arrives as "true" (with quotes).
        string result = await core.ExecuteScriptAsync(script.Replace("__CETUS_TEXT__", encoded));
        return result == "\"true\"";
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
            PostRightSidebarState();
            PostCetusSettingsState();
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
                else if (type.GetString() == "right-sidebar-toggle")
                {
                    _rightSidebarToggleRequested();
                }
                else if (type.GetString() == "modal"
                    && root.TryGetProperty("open", out JsonElement open))
                {
                    _dshModalChanged(open.GetBoolean());
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
                else if (type.GetString() == "dsh-session-selection")
                {
                    string? sessionId = root.TryGetProperty("sessionId", out JsonElement selection)
                        && selection.ValueKind == JsonValueKind.String
                        ? selection.GetString()
                        : null;
                    _currentSessionChanged?.Invoke(sessionId);
                }
                else if (type.GetString() == "cetus-open-port-settings")
                {
                    _openPortSettings?.Invoke();
                }
                else if (type.GetString() == "cetus-check-updates")
                {
                    _checkForUpdates?.Invoke();
                }
            }
        }
        catch (JsonException)
        {
            // Ignore messages not emitted by the document-created bridge.
        }
    }

    private void PostRightSidebarState()
    {
        if (!_initialized || _disposed || _view.CoreWebView2 is not { } core)
        {
            return;
        }

        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            source = WindowBridgeSource,
            type = "right-sidebar-state",
            open = _rightSidebarOpen,
        }));
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
            // External launch is best effort; in-app navigation remains blocked.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized && _view.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnTopLevelNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.WebMessageReceived -= OnWebMessageReceived;
        }
    }
}
