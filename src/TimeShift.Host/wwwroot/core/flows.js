// Flows: which panel opens for what, in which sheet. Designs call these and contain no flow logic.
import { h } from './dom.js';
import { openSheet, topSheet } from './sheet.js';
import { loginPanel } from './panels/login.js';
import { onboardingPanel } from './panels/onboarding.js';
import { switcherPanel } from './panels/switcher.js';
import { entryPanel } from './panels/entry.js';
import { gapPanel } from './panels/gap.js';
import { summaryPanel } from './panels/summary.js';
import { assistantPanel } from './panels/assistant.js';
import { settingsPanel } from './panels/settings.js';
import { takeReopen } from './designs.js';

const DEFAULT_SHEETS = {
  switcher: { detent: 'auto' },
  entry: { detent: 'auto' },
  gap: { detent: 'auto' },
  summary: { detent: 'large' },
  assistant: { detent: 'large' },
  settings: { detent: 'large' },
};

/**
 * @param {object} app  from createApp
 * @param {{ sheet?: object, sheets?: { [flow]: object }, autoPing?: boolean }} [opts]
 *   sheet  — options for every sheet (className, …); sheets — per flow overrides (switcher, entry, gap, summary,
 *   assistant, settings). autoPing (default true) opens the switcher when a ping is requested.
 */
export function createFlows(app, opts = {}) {
  const sheetOpts = (flow, extra) => ({ ...DEFAULT_SHEETS[flow], ...opts.sheet, ...opts.sheets?.[flow], ...extra,
    className: [opts.sheet?.className, opts.sheets?.[flow]?.className, `ts-sheet--${flow}`].filter(Boolean).join(' ') });
  const current = new Map(); // flow → sheet handle (one sheet per flow)

  function open(flow, make, extra) {
    current.get(flow)?.close('replaced');
    let handle = null;
    const panel = make({ close: () => handle?.close('done'), flows });
    handle = openSheet({ content: panel, ...sheetOpts(flow, extra), onClose: (reason) => { if (current.get(flow) === handle) current.delete(flow); opts.onClose?.(flow, reason); } });
    current.set(flow, handle);
    if (panel.focus) setTimeout(() => panel.focus.focus({ preventScroll: true }), 50);
    return handle;
  }

  const flows = {
    /** Route a piece of state.day to the right panel: running → switcher, questionable → gap, else entry. */
    openPiece(piece, { parallel = false } = {}) {
      if (!piece || piece.future) return null;
      if (piece.open && !parallel) return flows.openSwitcher();
      if (piece.status === 'questionable' && !parallel) return open('gap', (o) => gapPanel(app, { ...o, piece }));
      return open('entry', (o) => entryPanel(app, { ...o, piece, parallel }));
    },
    /** "What are you doing?" — { ping: true } when answering a notification. */
    openSwitcher({ ping = false } = {}) { return open('switcher', (o) => switcherPanel(app, { ...o, ping })); },
    /** New past entry; draft = { startMin, endMin, title?, category?, parallel? } on the viewed day. */
    openAdd(draft) {
      if (app.state.phase !== 'ready') return null;
      return open('entry', (o) => entryPanel(app, { ...o, draft }));
    },
    openEntry(piece, o2 = {}) { return open('entry', (o) => entryPanel(app, { ...o, piece, ...o2 })); },
    openGap(piece) { return open('gap', (o) => gapPanel(app, { ...o, piece })); },
    openAssistant(text) { return open('assistant', (o) => assistantPanel(app, { ...o, text })); },
    openSummary() { return open('summary', (o) => summaryPanel(app, o)); },
    /** page: open a section directly — 'schedule' | 'reminders' | 'tracking' | 'activities' | 'other'. */
    openSettings(page) { return open('settings', (o) => settingsPanel(app, { ...o, page })); },
    closeAll() { for (const s of [...current.values()]) s.close('program'); },
    get top() { return topSheet(); },

    /**
     * Render loading / login / onboarding into `container` (full screen) and keep it in sync with the phase.
     * The container gets data-phase and is hidden when the app is ready. Returns { destroy }.
     */
    renderGate(container) {
      let phase = null;
      let panel = null;
      const render = (s) => {
        const key = s.phase === 'loading' ? `loading:${s.bootError ? s.bootError.code : ''}` : s.phase;
        container.dataset.phase = s.phase;
        container.hidden = s.phase === 'ready';
        if (key === phase) return;
        phase = key;
        panel?.destroy?.();
        panel = null;
        if (s.phase === 'ready') { container.replaceChildren(); return; }
        if (s.phase === 'login') panel = loginPanel(app);
        else if (s.phase === 'onboarding') panel = onboardingPanel(app);
        else panel = loadingPanel(app, s);
        container.replaceChildren(h('div', { class: 'ts-gate', dataset: { phase: s.phase } }, panel.el));
        if (s.phase === 'login') container.querySelector('input')?.focus({ preventScroll: true });
      };
      const unsub = app.subscribe(render);
      return { destroy() { unsub(); panel?.destroy?.(); } };
    },
  };

  // After a design switch (actions.switchDesign) reopen the settings sheet where the user was. Once, never embedded.
  let reopenChecked = false;
  app.subscribe((s) => {
    if (reopenChecked || s.phase !== 'ready') return;
    reopenChecked = true;
    if (s.embed) return;
    const intent = takeReopen();
    if (intent?.flow === 'settings') flows.openSettings(intent.page);
  });

  // Pings: ?from=ping, a notification tap (service worker message), or app.actions.requestPing().
  if (opts.autoPing !== false) {
    app.subscribe((s) => {
      if (s.phase === 'ready' && s.ping.requested && s.day) {
        app.actions.clearPing();
        flows.openSwitcher({ ping: true });
      }
    });
  }
  return flows;
}

/** Loading screen: <div class="ts-loading-screen" data-offline> with a retry button when the first load failed. */
function loadingPanel(app, s) {
  const err = s.bootError;
  const el = h('div', { class: 'ts-loading-screen', dataset: { offline: err ? 'true' : 'false' }, 'aria-busy': err ? null : 'true' },
    h('p', { class: 'ts-loading-title' }, 'TimeShift'),
    h('p', { class: 'ts-loading-text', role: err ? 'alert' : 'status' }, err ? (err.code === 'offline' ? 'Нет сети. Как появится — откроется само.' : `Сервер не ответил как надо${err.status ? ` (${err.status})` : ''}. Попробуй ещё раз чуть позже.`) : 'Загружаю…'),
    err ? h('button', { type: 'button', class: 'ts-button', dataset: { variant: 'primary', action: 'retry' }, onClick: () => app.actions.retry() }, 'Повторить') : null);
  return { el };
}
