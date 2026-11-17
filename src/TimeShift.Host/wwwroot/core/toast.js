// One toast at a time: message, optional action («Отменить»), auto-hide, swipe down to dismiss.
import { h } from './dom.js';

let host = null;
let current = null;

function ensureHost() {
  if (host?.isConnected) return host;
  host = h('div', { class: 'ts-toast-host', 'data-ts-overlay': '', role: 'status', 'aria-live': 'polite' });
  document.body.append(host);
  return host;
}

/**
 * @param {{ message: string, action?: { label: string, onClick: Function }, kind?: 'info'|'error'|'success',
 *           duration?: number, className?: string }} opts
 * @returns {{ el, close() }}
 */
export function showToast(opts) {
  const root = ensureHost();
  current?.close('replaced');
  const duration = opts.duration ?? 6000;
  const msg = h('span', { class: 'ts-toast-message' }, opts.message);
  let actionBtn = null;
  if (opts.action) {
    actionBtn = h('button', { type: 'button', class: 'ts-toast-action', onClick: () => { t.close('action'); opts.action.onClick(); } }, opts.action.label);
  }
  const el = h('div', { class: ['ts-toast', opts.className], dataset: { kind: opts.kind ?? 'info', state: 'entering' }, role: opts.kind === 'error' ? 'alert' : null }, msg, actionBtn);
  root.append(el);
  let timer = 0;
  let closed = false;
  const t = {
    el,
    close(reason = 'program') {
      if (closed) return;
      closed = true;
      clearTimeout(timer);
      if (current === t) current = null;
      el.dataset.state = 'leaving';
      el.style.transform = '';
      el.style.opacity = '';
      const done = () => el.remove();
      el.addEventListener('transitionend', done, { once: true });
      setTimeout(done, 600);
      opts.onClose?.(reason);
    },
  };
  current = t;
  const arm = (ms) => { clearTimeout(timer); if (ms > 0) timer = setTimeout(() => t.close('timeout'), ms); };
  void el.offsetHeight;
  requestAnimationFrame(() => { if (!closed) el.dataset.state = 'shown'; });
  arm(duration);

  // Swipe down (or sideways) to dismiss; touching pauses the timer.
  let g = null;
  el.addEventListener('pointerdown', (e) => {
    if (e.target.closest('.ts-toast-action')) return;
    g = { id: e.pointerId, x: e.clientX, y: e.clientY, dx: 0, dy: 0 };
    el.setPointerCapture(e.pointerId);
    clearTimeout(timer);
    el.dataset.dragging = 'true';
  });
  el.addEventListener('pointermove', (e) => {
    if (!g || e.pointerId !== g.id) return;
    g.dx = e.clientX - g.x;
    g.dy = Math.max(0, e.clientY - g.y);
    const off = Math.abs(g.dx) > g.dy ? `${g.dx}px, 0` : `0, ${g.dy}px`;
    el.style.transform = `translate3d(${off}, 0)`;
    el.style.opacity = String(Math.max(0.2, 1 - Math.max(Math.abs(g.dx), g.dy) / 160));
  });
  const up = (e) => {
    if (!g || e.pointerId !== g.id) return;
    const far = g.dy > 36 || Math.abs(g.dx) > 80;
    g = null;
    delete el.dataset.dragging;
    if (far) t.close('swipe');
    else { el.style.transform = ''; el.style.opacity = ''; arm(Math.min(duration, 3000)); }
  };
  el.addEventListener('pointerup', up);
  el.addEventListener('pointercancel', up);
  return t;
}

export function hideToast() { current?.close('program'); }
