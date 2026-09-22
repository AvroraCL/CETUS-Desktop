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
        position: fixed; right: var(--cetus-update-right, 20px); bottom: 20px; z-index: 2147483000;
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
      if (primary.disabled) return;
      if (primary.dataset.mode === 'details') {
        postCetus({ type: 'cetus-update-details' });
        return;
      }
      if (primary.dataset.mode !== 'install') return;
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

  // Right inset that keeps the card clear of the Harness side panel.
  const sidebarWidth = () => {
    const margin = 20;
    let width = 0;
    const nodes = document.querySelectorAll('[data-sidebar-browser-frame], [data-slot^="sidebar"], aside');
    for (const node of nodes) {
      const rect = node.getBoundingClientRect && node.getBoundingClientRect();
      if (!rect || rect.width <= 0) continue;
      const fromRight = window.innerWidth - rect.right;
      if (fromRight < 80) width = Math.max(width, rect.width + fromRight);
    }
    return Math.round(width > 0 ? width + margin : margin);
  };

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

    // The Harness renders its own right sidebar (web browser, files,
    // terminal). Keep the card beside it instead of on top of it.
    ui.card.style.setProperty('--cetus-update-right', `${sidebarWidth()}px`);
    ui.card.style.display = '';
    ui.primary.disabled = false;
    ui.version.textContent = `当前 ${state.current || '—'} → 新版本 ${state.version}`;
    ui.notes.textContent = state.notes || '';
    ui.notes.style.display = state.notes ? '' : 'none';

    const busy = state.installing === true || state.installing === 'true';
    const ready = state.ready === true || state.ready === 'true';
    const installable = state.installable !== false && state.installable !== 'false';
    if (busy) {
      const progress = Number(state.progress);
      const known = Number.isFinite(progress) && progress > 0;
      ui.title.textContent = '正在准备更新…';
      ui.bar.style.display = known ? '' : 'none';
      ui.fill.style.width = known ? `${Math.round(progress * 100)}%` : '0%';
      ui.primary.textContent = known ? `${Math.round(progress * 100)}%` : '下载中…';
      ui.primary.disabled = true;
      ui.primary.dataset.mode = 'busy';
    } else if (!installable) {
      ui.title.textContent = '发现 CETUS 新版本';
      ui.bar.style.display = 'none';
      ui.primary.textContent = '查看发布页';
      ui.primary.dataset.mode = 'details';
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
