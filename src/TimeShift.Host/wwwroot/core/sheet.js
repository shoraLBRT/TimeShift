// Headless bottom sheet: structure, gestures, focus, history, keyboard. Designs style it entirely with CSS.
import { h, focusables, uid } from './dom.js';

const stack = [];
const config = {
  history: window.top === window,
  closeContent: null, // () => Node for the close button (designs bring their own icon)
  closeLabel: 'Закрыть',
};

/** Global options: { history: boolean, closeContent: () => Node, closeLabel }. */
export function configureSheets(opts) { Object.assign(config, opts); }
export const openSheetCount = () => stack.filter((s) => !s.closing).length;
export const topSheet = () => [...stack].reverse().find((s) => !s.closing)?.handle ?? null;
export function closeAllSheets(reason = 'program') { for (const s of [...stack].reverse()) s.handle.close(reason); }

const durationMs = (el) => {
  const d = getComputedStyle(el).transitionDuration.split(',')[0].trim();
  return d.endsWith('ms') ? parseFloat(d) : parseFloat(d) * 1000 || 0;
};
const rubber = (x, dim = 600) => (1 - 1 / ((x * 0.55) / dim + 1)) * dim;

/**
 * Open a bottom sheet.
 * @param {object} opts
 * @param {Node|{el: Node, title?: string, destroy?: Function}} opts.content  a node or a panel object
 * @param {string} [opts.title]           header title (defaults to the panel's title)
 * @param {string} [opts.className]       extra class on .ts-sheet
 * @param {'auto'|'large'} [opts.detent]  auto: as tall as the content (up to the max); large: always the max
 * @param {boolean} [opts.dismissible=true] drag, backdrop, Escape and back close it
 * @param {Function} [opts.onClose]       (reason) after the close animation
 * @param {string|Element} [opts.initialFocus] what to focus on open (the sheet itself by default: no keyboard pop-up)
 * @param {Node} [opts.footer]            optional fixed footer below the scroll area
 * @param {string} [opts.label]           accessible label when there is no visible title
 * @returns {{ el, body, header, close(reason?), setTitle(text), closed: Promise<string>, isOpen(): boolean }}
 */
export function openSheet(opts) {
  const panel = opts.content && !(opts.content instanceof Node) ? opts.content : null;
  const content = panel ? panel.el : opts.content;
  const dismissible = opts.dismissible !== false;
  const titleId = uid('ts-sheet-title');
  const titleEl = h('h2', { class: 'ts-sheet-title', id: titleId });
  const title = opts.title ?? panel?.title ?? '';
  titleEl.textContent = title;
  const closeBtn = dismissible ? h('button', { type: 'button', class: 'ts-sheet-close', 'aria-label': config.closeLabel, 'data-ts-close': '' }, config.closeContent?.()) : null;
  const header = h('header', { class: 'ts-sheet-header' }, titleEl, closeBtn);
  const body = h('div', { class: 'ts-sheet-body' }, content);
  const footer = opts.footer ? h('div', { class: 'ts-sheet-footer' }, opts.footer) : null;
  const backdrop = h('div', { class: 'ts-sheet-backdrop', 'aria-hidden': 'true', dataset: { state: 'opening' } });
  const el = h('section', {
    class: ['ts-sheet', opts.className], role: 'dialog', 'aria-modal': 'true', tabindex: '-1',
    'aria-labelledby': title ? titleId : null, 'aria-label': title ? null : opts.label ?? null,
    dataset: { state: 'opening', detent: opts.detent ?? 'auto' },
  }, h('div', { class: 'ts-sheet-grabber', 'aria-hidden': 'true' }), header, body, footer);
  if (!title) header.dataset.empty = 'true';

  let resolveClosed;
  const s = {
    el, backdrop, body, dismissible, closing: false, hist: false, prevFocus: document.activeElement, opts, panel,
    handle: null,
    subs: [], // in-sheet history steps: [{ index, onBack }]
  };
  const closed = new Promise((r) => { resolveClosed = r; });
  s.handle = {
    el, body, header, closed,
    isOpen: () => !s.closing && stack.includes(s),
    setTitle(text) {
      titleEl.textContent = text ?? '';
      header.dataset.empty = text ? 'false' : 'true';
      if (text) { el.setAttribute('aria-labelledby', titleId); el.removeAttribute('aria-label'); }
    },
    close: (reason = 'program') => closeSheet(s, reason),
    /**
     * Make the system back action undo one step inside this sheet (e.g. a settings sub-page) instead of closing it.
     * onBack runs when the user goes back; call release() when the step is undone from the UI instead.
     * Without history (iframe, non-dismissible sheet) it does nothing and release() is a no-op.
     */
    pushHistoryEntry(onBack) {
      if (!s.hist || s.closing || pendingBack || stack.at(-1) !== s) return { release() {} };
      histDepth += 1;
      const sub = { index: histDepth, onBack };
      s.subs.push(sub);
      history.pushState({ ...(history.state ?? {}), tsSheet: histDepth }, '');
      return {
        release() {
          if (s.closing || s.subs.at(-1) !== sub) { const i = s.subs.indexOf(sub); if (i >= 0 && !s.closing) s.subs.splice(i, 1); return; }
          s.subs.pop();
          popHistory(1);
        },
      };
    },
  };
  s.finish = (reason) => resolveClosed(reason);

  stack.push(s);
  document.body.append(backdrop, el);
  updateLayers();
  if (dismissible && config.history) pushHistory(s);

  backdrop.addEventListener('click', () => { if (s.dismissible && isTop(s)) s.handle.close('backdrop'); });
  backdrop.addEventListener('touchmove', (e) => { if (e.cancelable) e.preventDefault(); }, { passive: false });
  el.addEventListener('click', (e) => {
    const c = e.target.closest('[data-ts-close]');
    if (c && el.contains(c) && c.closest('.ts-sheet') === el) s.handle.close('button');
  });
  el.addEventListener('focusin', (e) => scheduleReveal(e.target));
  attachDrag(s);

  // Enter: commit the off-screen position, then animate to open.
  void el.offsetHeight;
  requestAnimationFrame(() => {
    el.dataset.state = 'open';
    backdrop.dataset.state = 'open';
    const f = typeof opts.initialFocus === 'string' ? el.querySelector(opts.initialFocus) : opts.initialFocus;
    (f ?? el).focus({ preventScroll: true });
  });
  panel?.onShow?.(s.handle);
  return s.handle;
}

const isTop = (s) => openStack().at(-1) === s;
const openStack = () => stack.filter((x) => !x.closing);

function closeSheet(s, reason, { fromHistory = false, velocity = 0, from = null } = {}) {
  if (s.closing || !stack.includes(s)) return s.handle.closed;
  // Closing a sheet closes the ones opened from it first.
  const above = stack.slice(stack.indexOf(s) + 1).filter((x) => !x.closing);
  for (const x of above.reverse()) closeSheet(x, 'parent', { fromHistory: true });
  s.closing = true;
  const entries = (x) => (x.hist ? 1 + x.subs.length : 0);
  if (s.hist && !fromHistory) popHistory(entries(s) + above.reduce((n, x) => n + entries(x), 0));
  s.subs.length = 0;
  for (const x of above) x.subs.length = 0;
  const { el, backdrop } = s;
  const full = durationMs(el);
  if (from != null && velocity > 0) {
    const h = el.offsetHeight;
    const ms = Math.max(120, Math.min(full, (h - from) / velocity));
    el.style.transitionDuration = `${ms}ms`;
    backdrop.style.transitionDuration = `${ms}ms`;
  }
  delete el.dataset.dragging;
  el.dataset.state = 'closing';
  backdrop.dataset.state = 'closing';
  el.style.transform = '';
  backdrop.style.opacity = '';
  let done = false;
  const finish = () => {
    if (done) return;
    done = true;
    el.remove();
    backdrop.remove();
    stack.splice(stack.indexOf(s), 1);
    updateLayers();
    const prev = s.prevFocus;
    if (prev && prev.isConnected && !prev.closest('[inert]')) prev.focus({ preventScroll: true });
    else openStack().at(-1)?.el.focus({ preventScroll: true });
    try { s.panel?.destroy?.(); } finally {
      s.opts.onClose?.(reason);
      s.finish(reason);
    }
  };
  el.addEventListener('transitionend', (e) => { if (e.target === el && e.propertyName === 'transform') finish(); });
  setTimeout(finish, (parseFloat(el.style.transitionDuration) || full) + 120);
  return s.handle.closed;
}

// ---------- layers: inert, scroll lock, depth ----------

const inerted = new Set();
function updateLayers() {
  const open = openStack();
  const top = open.at(-1);
  document.documentElement.classList.toggle('ts-sheet-open', stack.length > 0);
  for (const node of [...inerted]) {
    if (!top || node === top.el || node === top.backdrop || !node.isConnected) { node.inert = false; inerted.delete(node); }
  }
  if (top) {
    for (const node of document.body.children) {
      if (node === top.el || node === top.backdrop || node.hasAttribute('data-ts-overlay') || node.tagName === 'SCRIPT') continue;
      if (!node.inert) { node.inert = true; inerted.add(node); }
    }
  }
  open.forEach((x, i) => {
    const depth = String(open.length - 1 - i);
    x.el.dataset.depth = depth;
    x.backdrop.dataset.depth = depth;
    x.el.dataset.stacked = i < open.length - 1 ? 'true' : 'false';
  });
}

// ---------- history: the back action closes the top sheet ----------

let histDepth = 0;
let pendingBack = 0;
let backTimer = 0;
const queued = [];

function pushHistory(s) {
  if (pendingBack) { queued.push(s); return; }
  s.hist = true;
  histDepth += 1;
  s.histIndex = histDepth;
  history.pushState({ ...(history.state ?? {}), tsSheet: histDepth }, '');
}
function popHistory(n) {
  histDepth = Math.max(0, histDepth - n);
  pendingBack += 1;
  history.go(-n);
  clearTimeout(backTimer);
  backTimer = setTimeout(() => { if (pendingBack) { pendingBack = 0; flushQueued(); } }, 600);
}
function flushQueued() {
  while (queued.length) { const s = queued.shift(); if (!s.closing && stack.includes(s)) pushHistory(s); }
}
window.addEventListener('popstate', (e) => {
  if (pendingBack) {
    pendingBack -= 1;
    if (!pendingBack) { clearTimeout(backTimer); flushQueued(); }
    return;
  }
  const depth = e.state?.tsSheet ?? 0;
  // Undo, from the top, every sheet and every in-sheet step (pushHistoryEntry) above the new history position.
  const items = [];
  for (const s of stack) {
    if (!s.hist || s.closing) continue;
    if (s.histIndex > depth) items.push({ index: s.histIndex, sheet: s });
    for (const sub of s.subs) if (sub.index > depth) items.push({ index: sub.index, sheet: s, sub });
  }
  histDepth = Math.min(histDepth, depth);
  items.sort((a, b) => b.index - a.index);
  for (const it of items) {
    if (it.sheet.closing) continue;
    if (it.sub) {
      it.sheet.subs.splice(it.sheet.subs.indexOf(it.sub), 1);
      try { it.sub.onBack(); } catch (err) { console.error(err); }
    } else closeSheet(it.sheet, 'back', { fromHistory: true });
  }
});

// ---------- keyboard: Escape, Tab trap ----------

document.addEventListener('keydown', (e) => {
  const top = openStack().at(-1);
  if (!top) return;
  if (e.key === 'Escape' && top.dismissible && !e.defaultPrevented) {
    e.preventDefault();
    top.handle.close('escape');
  } else if (e.key === 'Tab') {
    const list = focusables(top.el);
    if (!list.length) { e.preventDefault(); top.el.focus(); return; }
    const first = list[0];
    const last = list.at(-1);
    if (e.shiftKey && (document.activeElement === first || document.activeElement === top.el)) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    else if (!top.el.contains(document.activeElement)) { e.preventDefault(); first.focus(); }
  }
});

// ---------- on-screen keyboard: keep the focused field visible ----------

const vv = window.visualViewport;
function syncViewport() {
  if (!vv) return;
  const inset = Math.max(0, Math.round(window.innerHeight - vv.height - vv.offsetTop));
  const root = document.documentElement.style;
  root.setProperty('--ts-keyboard-inset', `${inset}px`);
  root.setProperty('--ts-vvh', `${Math.round(vv.height)}px`);
  document.documentElement.classList.toggle('ts-keyboard-open', inset > 80);
  const a = document.activeElement;
  if (a && a.closest?.('.ts-sheet')) scheduleReveal(a);
}
if (vv) {
  let raf = 0;
  const on = () => { cancelAnimationFrame(raf); raf = requestAnimationFrame(syncViewport); };
  vv.addEventListener('resize', on, { passive: true });
  vv.addEventListener('scroll', on, { passive: true });
  syncViewport();
}

let revealTimer = 0;
function scheduleReveal(target) {
  if (!target?.matches?.('input,textarea,select,[contenteditable="true"]')) return;
  clearTimeout(revealTimer);
  revealTimer = setTimeout(() => reveal(target), 280);
}
function reveal(target) {
  const scroller = target.closest('.ts-sheet-body');
  if (!scroller || document.activeElement !== target) return;
  const r = target.getBoundingClientRect();
  const visibleBottom = vv ? vv.offsetTop + vv.height : window.innerHeight;
  const box = scroller.getBoundingClientRect();
  const bottom = Math.min(box.bottom, visibleBottom) - 16;
  const top = box.top + 8;
  if (r.bottom > bottom) scroller.scrollBy({ top: r.bottom - bottom, behavior: 'smooth' });
  else if (r.top < top) scroller.scrollBy({ top: r.top - top, behavior: 'smooth' });
}

// ---------- drag to dismiss ----------

function scrollableY(node) {
  if (node.scrollHeight <= node.clientHeight + 1) return false;
  const oy = getComputedStyle(node).overflowY;
  return oy === 'auto' || oy === 'scroll';
}

function attachDrag(s) {
  const { el, backdrop, body } = s;
  let g = null;

  const begin = (x, y, target) => {
    if (s.closing || target.closest('[data-ts-nodrag],input[type=range]')) return null;
    const handle = target.closest('.ts-sheet-grabber,.ts-sheet-header,.ts-sheet-footer');
    let scroller = null;
    for (let n = target; n && n !== el; n = n.parentElement) {
      if (n === body || (body.contains(n) && scrollableY(n))) { scroller = n; break; }
    }
    return { x0: x, y0: y, mode: 'pending', zone: handle ? 'handle' : 'body', scroller: scroller ?? body, samples: [[performance.now(), y]], dy: 0, height: 0 };
  };

  const decide = (x, y) => {
    const dx = x - g.x0;
    const dy = y - g.y0;
    if (Math.abs(dx) < 6 && Math.abs(dy) < 6) return;
    if (Math.abs(dx) > Math.abs(dy)) { g.mode = 'none'; return; }
    const canScroll = scrollableY(g.scroller);
    if (g.zone === 'handle' || !canScroll || (g.scroller.scrollTop <= 0 && dy > 0)) {
      g.mode = 'drag';
      g.y0 = y;
      g.height = el.offsetHeight;
      el.dataset.dragging = 'true';
      el.style.transition = 'none';
      backdrop.style.transition = 'none';
    } else g.mode = 'none';
  };

  const move = (y) => {
    const now = performance.now();
    g.samples.push([now, y]);
    while (g.samples.length > 2 && now - g.samples[0][0] > 100) g.samples.shift();
    const dy = y - g.y0;
    g.dy = dy;
    let t;
    if (dy < 0) t = -rubber(-dy, 80);
    else t = s.dismissible ? dy : rubber(dy, 120);
    el.style.transform = `translate3d(0, ${t}px, 0)`;
    backdrop.style.opacity = String(Math.max(0, 1 - Math.max(0, t) / (g.height || 1)));
  };

  const end = () => {
    const [t0, y0] = g.samples[0];
    const [t1, y1] = g.samples.at(-1);
    const v = t1 > t0 ? (y1 - y0) / (t1 - t0) : 0; // px/ms, + is down
    const dy = g.dy;
    const h = g.height || el.offsetHeight;
    g = null;
    const dismiss = s.dismissible && dy > 0 && (dy > h * 0.45 || (v > 0.45 && dy > 12) || dy + v * 220 > h * 0.6);
    el.style.transition = '';
    backdrop.style.transition = '';
    if (dismiss) { closeSheet(s, 'drag', { velocity: Math.max(v, 0.6), from: Math.max(0, dy) }); return; }
    delete el.dataset.dragging;
    el.style.transform = '';
    backdrop.style.opacity = '';
  };

  // Touch: one listener set for handle and body (body drags only when scrolled to the top).
  el.addEventListener('touchstart', (e) => {
    if (e.touches.length !== 1) { if (g?.mode === 'drag') end(); g = null; return; }
    const t = e.touches[0];
    g = begin(t.clientX, t.clientY, e.target);
  }, { passive: true });
  el.addEventListener('touchmove', (e) => {
    if (!g) return;
    const t = e.touches[0];
    if (g.mode === 'pending') {
      decide(t.clientX, t.clientY);
      if (g.mode === 'pending' && g.zone === 'handle' && e.cancelable) e.preventDefault();
    }
    if (g.mode === 'drag') { if (e.cancelable) e.preventDefault(); move(t.clientY); }
    else if (g.mode === 'none' && !scrollableY(g.scroller) && e.cancelable) e.preventDefault(); // no scroll chaining
  }, { passive: false });
  const touchEnd = () => { if (g?.mode === 'drag') end(); else g = null; };
  el.addEventListener('touchend', touchEnd, { passive: true });
  el.addEventListener('touchcancel', touchEnd, { passive: true });

  // Mouse / pen: drag from the grabber or the header (desktop development).
  el.addEventListener('pointerdown', (e) => {
    if (e.pointerType === 'touch' || e.button !== 0 || !e.target.closest('.ts-sheet-grabber,.ts-sheet-header')) return;
    if (e.target.closest('button,a,input,select,textarea')) return;
    g = begin(e.clientX, e.clientY, e.target);
    if (!g) return;
    const id = e.pointerId;
    el.setPointerCapture(id);
    const onMove = (ev) => {
      if (!g || ev.pointerId !== id) return;
      if (g.mode === 'pending') decide(ev.clientX, ev.clientY);
      if (g.mode === 'drag') move(ev.clientY);
    };
    const onUp = (ev) => {
      if (ev.pointerId !== id) return;
      el.removeEventListener('pointermove', onMove);
      el.removeEventListener('pointerup', onUp);
      el.removeEventListener('pointercancel', onUp);
      if (g?.mode === 'drag') end(); else g = null;
    };
    el.addEventListener('pointermove', onMove);
    el.addEventListener('pointerup', onUp);
    el.addEventListener('pointercancel', onUp);
  });
}
