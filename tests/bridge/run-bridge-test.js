// Runs the injected WebView2 bridge script against a minimal DOM shim, so the
// update card is executed for real instead of only being syntax-checked.
// Usage: node run-bridge-test.js <extracted.js>
const fs = require('fs');
const vm = require('vm');

const source = fs.readFileSync(process.argv[2], 'utf8');

let failures = 0;
const check = (name, condition, extra) => {
  if (condition) {
    console.log('ok   ' + name);
  } else {
    failures++;
    console.log('FAIL ' + name + (extra === undefined ? '' : ' -> ' + extra));
  }
};

// ── minimal DOM ───────────────────────────────────────────────────────────
const allElements = [];
let idCounter = 0;

class ClassList {
  constructor() { this.items = new Set(); }
  add(...names) { names.forEach((n) => this.items.add(n)); }
  contains(name) { return this.items.has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toUpperCase();
    this.children = [];
    this.parent = null;
    this.classList = new ClassList();
    this.dataset = {};
    this.attributes = {};
    this.listeners = {};
    this.style = {
      _props: {},
      setProperty(name, value) { this._props[name] = value; },
      getPropertyValue(name) { return this._props[name] ?? ''; },
    };
    this.textContent = '';
    this.disabled = false;
    this._id = '';
    this._className = '';
    this._uid = ++idCounter;
    allElements.push(this);
  }
  set id(value) { this._id = value; this.attributes.id = value; }
  get id() { return this._id; }
  set className(value) {
    this._className = value;
    String(value).split(/\s+/).filter(Boolean).forEach((n) => this.classList.add(n));
  }
  get className() { return this._className; }
  get parentElement() { return this.parent; }
  getBoundingClientRect() { return { right: 0, width: 0, top: 0, left: 0, bottom: 0, height: 0 }; }
  setAttribute(name, value) { this.attributes[name] = String(value); if (name === 'id') this._id = String(value); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  removeAttribute(name) { delete this.attributes[name]; }
  appendChild(child) { child.parent = this; this.children.push(child); return child; }
  replaceChildren(...nodes) { this.children = []; nodes.forEach((n) => this.appendChild(n)); }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter((c) => c !== this); this.parent = null; }
  addEventListener(type, handler) { (this.listeners[type] ||= []).push(handler); }
  dispatch(type, event = {}) { (this.listeners[type] || []).forEach((h) => h({ target: this, ...event })); }
  querySelectorAll(selector) { return queryFrom(this, selector); }
  querySelector(selector) { return queryFrom(this, selector)[0] ?? null; }
  descendants() {
    const found = [];
    const walk = (n) => { for (const c of n.children) { found.push(c); walk(c); } };
    walk(this);
    return found;
  }
}

/// Supports the selectors the bridge uses: tag, .class, #id and one or more
/// [attr="value"] clauses (for example div[role="dialog"][aria-modal="true"]).
function matches(element, part) {
  const tokenPattern = /\[([a-zA-Z-]+)="([^"]*)"\]|([.#]?)([a-zA-Z-]+)/g;
  let consumed = 0;
  let match;
  while ((match = tokenPattern.exec(part)) !== null) {
    consumed += match[0].length;
    if (match[1] !== undefined) {
      if (element.getAttribute(match[1]) !== match[2]) return false;
      continue;
    }
    const [, prefix, name] = [match[0], match[3], match[4]];
    if (prefix === '#') {
      if (element.id !== name) return false;
    } else if (prefix === '.') {
      if (!element.classList.contains(name)) return false;
    } else if (element.tagName !== name.toUpperCase()) {
      return false;
    }
  }
  return consumed === part.length;
}

/// Supports the selectors the bridge uses: tag, .class, #id and [attr="value"].
function queryFrom(root, selector) {
  const parts = selector.replace(/\s+/g, ' ').trim().split(' ');
  let current = [root];
  for (const part of parts) {
    const next = [];
    for (const node of current) {
      for (const child of node.descendants()) {
        if (matches(child, part)) next.push(child);
      }
    }
    current = next;
  }
  return current;
}

const document = {
  head: new Element('head'),
  body: new Element('body'),
  documentElement: new Element('html'),
  readyState: 'complete',
  _listeners: {},
  createElement: (tag) => new Element(tag),
  getElementById: (id) => document.documentElement.descendants().find((e) => e.id === id) ?? null,
  addEventListener(type, handler) { (this._listeners[type] ||= []).push(handler); },
  dispatch(type, event = {}) { (this._listeners[type] || []).forEach((h) => h(event)); },
  dispatch(type, event = {}) { (this._listeners[type] || []).forEach((h) => h(event)); },
  querySelectorAll: (selector) => queryFrom(document.documentElement, selector),
  querySelector: (selector) => queryFrom(document.documentElement, selector)[0] ?? null,
};
document.documentElement.appendChild(document.head);
document.documentElement.appendChild(document.body);

// The settings dialog the bridge augments: [role=dialog][aria-modal=true]
// containing a [data-slot="settings.general.item"] inside a section.
const dialog = document.createElement('div');
dialog.setAttribute('role', 'dialog');
dialog.setAttribute('aria-modal', 'true');
const section = document.createElement('div');
const container = document.createElement('div');
container.setAttribute('data-slot', 'settings.general.item');
section.appendChild(container);
dialog.appendChild(section);
document.body.appendChild(dialog);

// ── WebView2 bridge shim ──────────────────────────────────────────────────
const posted = [];
const webviewListeners = [];
const sandbox = {
  console, document, MutationObserver: class { observe() {} disconnect() {} },
  setTimeout, clearTimeout, Number, String, Math, JSON, Date, Array, Object, Error,
  getComputedStyle: () => ({ colorScheme: 'light' }),
};
sandbox.window = {
  chrome: {
    webview: {
      postMessage: (message) => posted.push(message),
      addEventListener: (type, handler) => webviewListeners.push(handler),
    },
  },
  matchMedia: () => ({ matches: false, addEventListener() {} }),
  innerWidth: 1400,
  innerHeight: 900,
};
sandbox.globalThis = sandbox;

vm.createContext(sandbox);
vm.runInContext(source, sandbox, { filename: 'bridge.js' });

document.readyState = 'complete';
document.dispatch('DOMContentLoaded', {});

const receive = (message) => webviewListeners.forEach((h) => h({ data: message }));
const card = () => document.getElementById('cetus-update-card');
const byClass = (name) => allElements.filter((e) => e.classList.contains(name));

// 1. no update -> nothing rendered
receive({ source: 'cetus-window', type: 'cetus-update-state', update: { available: false } });
check('no card when no update is available', card() === null);

// 2. an available update renders the card
receive({
  source: 'cetus-window',
  type: 'cetus-update-state',
  update: {
    available: true, dismissed: false, version: 'v0.3.4', versionNumber: '0.3.4',
    current: '0.3.3', notes: '修复内容\n第二行', installing: false, progress: 0, source: 'github',
  },
});
check('card rendered for an available update', card() !== null);
check('card shows the version pair',
  (byClass('cetus-update-version')[0] || {}).textContent === '当前 0.3.3 → 新版本 v0.3.4',
  (byClass('cetus-update-version')[0] || {}).textContent);
check('card shows release notes', (byClass('cetus-update-notes')[0] || {}).textContent === '修复内容\n第二行');
check('primary action offered', (byClass('cetus-update-primary')[0] || {}).textContent === '立即更新',
  (byClass('cetus-update-primary')[0] || {}).textContent);

// 3. primary click asks C# to install
byClass('cetus-update-primary')[0].dispatch('click');
check('install message posted', posted.some((m) => m.type === 'cetus-update-install'),
  JSON.stringify(posted.map((m) => m.type)));

// A development build may show the release but cannot install over its build tree.
const installCount = posted.filter((m) => m.type === 'cetus-update-install').length;
receive({
  source: 'cetus-window',
  type: 'cetus-update-state',
  update: {
    available: true, version: 'v0.3.4', current: '0.3.3',
    installing: false, progress: 0, installable: false,
  },
});
const devAction = byClass('cetus-update-primary')[0];
check('development action opens release page', devAction.textContent === '查看发布页');
devAction.dispatch('click');
check('development action never requests install',
  posted.filter((m) => m.type === 'cetus-update-install').length === installCount);
check('development action requests release details',
  posted.some((m) => m.type === 'cetus-update-details'));

// 4. busy state disables the button and shows progress
receive({
  source: 'cetus-window',
  type: 'cetus-update-state',
  update: {
    available: true, dismissed: false, version: 'v0.3.4', current: '0.3.3',
    notes: '', installing: true, progress: 0.5, source: 'github',
  },
});
const busy = byClass('cetus-update-primary')[0];
check('button disabled while installing', busy.disabled === true, String(busy.disabled));
check('progress shown on the button', busy.textContent === '50%', busy.textContent);

// 5. dismiss hides the card and tells C#
const closeButton = byClass('cetus-update-close')[0];
check('close button present', closeButton !== undefined);
const cardBefore = card();
closeButton.dispatch('click');
const cardAfter = card();
check('card removed after dismiss', cardAfter === null, cardBefore === cardAfter ? 'same node still mounted' : 'other');
check('dismiss message posted', posted.some((m) => m.type === 'cetus-update-dismiss'));

// 6. the settings row keeps a way back to the notice
receive({ source: 'cetus-window', type: 'cetus-settings-state', values: { dshPort: '3080', dshVersion: '0.1.7-alpha.1' } });
receive({
  source: 'cetus-window',
  type: 'cetus-update-state',
  update: { available: true, dismissed: false, version: 'v0.3.4', current: '0.3.3', notes: '', installing: false, progress: 0 },
});
const pill = document.getElementById('cetus-setting-update');
check('settings row built inside the dialog', pill !== null);

check('settings row advertises the new version', pill !== null && pill.textContent === '新版本 v0.3.4',
  pill && pill.textContent);

// 7. the bridge asks for state so a reloaded page still shows the notice
check('state requested on load', posted.some((m) => m.type === 'cetus-update-state-request'),
  JSON.stringify(posted.map((m) => m.type)));

console.log(failures === 0 ? '\nALL BRIDGE CHECKS PASSED' : `\n${failures} BRIDGE CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
