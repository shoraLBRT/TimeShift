// Direct manipulation of the day's timeline.
//   long press a stored entry            → handles on its edges; drag one to move that boundary
//   long press unrecorded time and drag  → a new entry for the interval (the entry sheet asks what it was)
// Geometry comes from layoutDay; the change goes through app.actions, so the server displaces the neighbours
// and journals one undo step. Nothing here decides what happens to the neighbours.
import { h } from './dom.js';
import { blockAt, pieceBounds } from './timeline.js';
import { fmtClock, fmtRange, fmtDuration, minToLocal, roundTo, floorTo } from './time.js';

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const px = (n) => `${Math.round(n * 100) / 100}px`;
const buzz = (n) => { try { navigator.vibrate?.(n); } catch { /* not allowed here */ } };

/** Where a long press lands: 'entry' (a stored past entry), 'free' (unrecorded past time) or null (nothing to do). */
export function pressTarget(layout, day, min) {
  if (!layout || !day || min < 0) return null;
  const limit = Math.min(layout.dayMinutes, day.nowMin ?? layout.dayMinutes);
  const block = blockAt(layout.blocks, layout.yOf(min));
  const piece = block?.piece ?? null;
  if (piece?.future) return null;
  if (piece?.segment?.id && !String(piece.segment.id).startsWith('local:')) return { kind: 'entry', piece };
  return min <= limit ? { kind: 'free', piece } : null;
}

/** Drawn bounds of a stored entry on the viewed day and which of its edges can be dragged there. */
export function entryEdges(piece, day, limit) {
  const b = pieceBounds(piece, day);
  const open = b.end == null;
  return {
    startMin: Math.max(0, b.startMin),
    endMin: open ? limit : Math.min(day.dayMinutes, b.endMin),
    open,
    start: b.startMin >= 0,                       // an edge on another day is edited in the sheet
    end: !open && b.endMin <= day.dayMinutes,     // the running entry has no end to drag
  };
}

/** A dragged boundary snapped to the rounding step and kept inside its limits. */
export function snapEdge(edge, min, { startMin, endMin, limit, step }) {
  const len = Math.max(1, Math.min(step, endMin - startMin));
  const v = roundTo(min, step);
  return edge === 'start' ? clamp(v, 0, endMin - len) : clamp(v, startMin + len, limit);
}

/** Interval of a new entry: dragged out from `anchor`, or a default around the press when the finger did not move. */
export function newInterval({ anchor, value, at, limit, step, piece }) {
  let a = Math.min(anchor, value);
  let b = Math.max(anchor, value);
  if (b - a < Math.max(1, step)) {
    a = clamp(floorTo(at, 15), 0, Math.max(0, limit - 15));
    b = Math.min(limit, a + 60);
    if (piece) { a = Math.max(a, piece.startMin); b = Math.min(b, piece.endMin); } // stay inside the unrecorded piece
  }
  return b - a >= 1 ? { startMin: a, endMin: b } : null;
}

/**
 * @param {object} app    from createApp
 * @param {object} flows  from createFlows (the new-entry sheet)
 * @param {object} o
 * @param {Element} o.scroller                     the vertical scroll container of the timeline
 * @param {Element | (() => Element)} o.surface    positioned element whose top edge is 00:00 of the viewed day and
 *                                                 whose height is layout.height; the overlay is appended to it
 * @param {() => object} o.layout                  the current layoutDay() result of the viewed day (or null)
 * @param {string | ((target: Element) => boolean)} [o.ignore]  press targets to leave alone (buttons, parallel notes)
 * @param {() => { top?: number, bottom?: number }} [o.insets]  px of the scroller hidden under bars (auto-scroll)
 * @param {number} [o.ms=450]  [o.tolerance=10]    long press: hold time and allowed movement
 * @returns {{ cancel(), destroy(), readonly active: boolean }}
 */
export function timelineDrag(app, flows, o) {
  const scroller = o.scroller;
  const surfaceOf = typeof o.surface === 'function' ? o.surface : () => o.surface;
  const ms = o.ms ?? 450;
  const tol = o.tolerance ?? 10;
  const ignored = (t) => (typeof o.ignore === 'function' ? !!o.ignore(t) : o.ignore ? !!t.closest(o.ignore) : false);

  const time = h('span', { class: 'ts-drag-clock' });
  const dur = h('span', { class: 'ts-drag-duration' });
  const label = h('div', { class: 'ts-drag-time', hidden: true }, time, dur);
  const handles = {
    start: h('div', { class: 'ts-drag-handle', dataset: { edge: 'start' } }),
    end: h('div', { class: 'ts-drag-handle', dataset: { edge: 'end' } }),
  };
  // The editor sheet stays the keyboard and screen-reader way to change bounds.
  const overlay = h('div', { class: 'ts-drag', hidden: true, 'aria-hidden': 'true', dataset: { tsNoswipe: '' } }, label, handles.start, handles.end);

  let press = null;        // waiting for the hold: { id, x, y, surface, timer }
  let held = null;         // pointer that fired the long press and is still down (the page must not scroll under it)
  let sel = null;          // entry with handles: { id, surface, startMin, endMin, open, start, end, title, category }
  let drag = null;         // { kind: 'edge' | 'new', id, surface, step, limit, value, ... }
  let lastX = 0;
  let lastY = 0;
  let raf = 0;
  let swallowUntil = 0;

  const ctx = () => {
    const s = app.state;
    const L = o.layout();
    const day = s.day;
    if (s.phase !== 'ready' || !L || !day || day.date !== s.viewDate || s.viewDate > s.today) return null;
    return { s, L, day, limit: Math.min(L.dayMinutes, day.nowMin ?? L.dayMinutes), step: s.me?.settings?.roundingMin ?? 5 };
  };
  const minAt = (surface, L, clientY) => L.minOf(clientY - surface.getBoundingClientRect().top);

  function show(surface, a, b, { mode, edge = null, dragging = false, text = null }) {
    const L = o.layout();
    if (!L) return;
    if (overlay.parentNode !== surface) surface.append(overlay);
    overlay.style.top = px(L.yOf(a));
    overlay.style.height = px(L.yOf(b) - L.yOf(a));
    overlay.dataset.mode = mode;
    if (edge) overlay.dataset.edge = edge; else delete overlay.dataset.edge;
    if (dragging) overlay.dataset.dragging = 'true'; else delete overlay.dataset.dragging;
    handles.start.hidden = mode !== 'resize' || !sel?.start;
    handles.end.hidden = mode !== 'resize' || !sel?.end;
    label.hidden = !text;
    if (text) {
      time.textContent = text;
      dur.textContent = b > a ? fmtDuration(b - a) : '';
      const r = surface.getBoundingClientRect();
      overlay.style.setProperty('--ts-drag-x', px(clamp(lastX - r.left, 0, r.width)));
    }
    overlay.hidden = false;
  }
  const hide = () => { overlay.hidden = true; delete overlay.dataset.dragging; };
  const showSel = () => show(sel.surface, sel.startMin, sel.endMin, { mode: 'resize' });

  function cancelPress() { if (press) { clearTimeout(press.timer); press = null; } }
  function cancel() {
    cancelPress();
    drag = null;
    sel = null;
    held = null;
    if (raf) { cancelAnimationFrame(raf); raf = 0; }
    hide();
  }

  // ----- long press -----
  const down = (e) => {
    if (press || drag || held !== null || (e.pointerType === 'mouse' && e.button !== 0)) return;
    if (overlay.contains(e.target)) return;
    const surface = surfaceOf();
    if (!surface || !surface.contains(e.target) || ignored(e.target)) return;
    if (!ctx() || app.state.busy) return;
    press = { id: e.pointerId, x: e.clientX, y: e.clientY, surface, timer: setTimeout(fire, ms) };
  };

  function fire() {
    const p = press;
    press = null;
    const c = ctx();
    if (!p || !c || c.s.busy || !p.surface.isConnected) return;
    const at = minAt(p.surface, c.L, p.y);
    const target = pressTarget(c.L, c.day, at);
    if (!target) return;
    lastX = p.x;
    lastY = p.y;
    if (target.kind === 'entry') {
      const e = entryEdges(target.piece, c.day, c.limit);
      if (!e.start && !e.end) return;
      sel = { id: target.piece.segment.id, surface: p.surface, title: target.piece.title, category: target.piece.category ?? null, ...e };
      held = p.id;
      buzz(10);
      showSel();
      return;
    }
    sel = null;
    held = p.id;
    const anchor = clamp(roundTo(at, c.step), 0, c.limit);
    drag = { kind: 'new', id: p.id, surface: p.surface, step: c.step, limit: c.limit, at, anchor, value: anchor, piece: target.piece };
    buzz(10);
    paint();
  }

  // ----- handles -----
  const handleDown = (e) => {
    const el = e.target.closest('.ts-drag-handle');
    if (!el || !sel || drag || (e.pointerType === 'mouse' && e.button !== 0)) return;
    e.stopPropagation(); // not a swipe, not a new long press
    const c = ctx();
    if (!c || c.s.busy) return;
    e.preventDefault();
    try { el.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    const edge = el.dataset.edge;
    const from = edge === 'start' ? sel.startMin : sel.endMin;
    lastX = e.clientX;
    lastY = e.clientY;
    // grab: where inside the handle the finger landed, so the edge does not jump on the first move.
    drag = { kind: 'edge', id: e.pointerId, surface: sel.surface, step: c.step, limit: c.limit, edge, from, value: from, grab: minAt(sel.surface, c.L, e.clientY) - from };
    paint();
  };

  function paint() {
    const d = drag;
    if (d.kind === 'edge') {
      const a = d.edge === 'start' ? d.value : sel.startMin;
      const b = d.edge === 'end' ? d.value : sel.endMin;
      show(d.surface, a, b, { mode: 'resize', edge: d.edge, dragging: true, text: fmtClock(d.value) });
    } else {
      const a = Math.min(d.anchor, d.value);
      const b = Math.max(d.anchor, d.value);
      show(d.surface, a, b, { mode: 'new', edge: d.value < d.anchor ? 'start' : 'end', dragging: true, text: b > a ? fmtRange(a, b) : fmtClock(a) });
    }
  }

  function update() {
    const d = drag;
    const L = o.layout();
    if (!d || !L) return;
    const raw = minAt(d.surface, L, lastY) - (d.grab ?? 0);
    const v = d.kind === 'edge'
      ? snapEdge(d.edge, raw, { startMin: sel.startMin, endMin: sel.endMin, limit: d.limit, step: d.step })
      : clamp(roundTo(raw, d.step), 0, d.limit);
    if (v !== d.value) { d.value = v; buzz(5); }
    paint();
  }

  // Near the top or bottom of the visible timeline the page scrolls under the finger.
  function autoScroll() {
    raf = 0;
    if (!drag) return;
    const r = scroller.getBoundingClientRect();
    const ins = o.insets?.() ?? {};
    const zone = 56;
    const top = r.top + (ins.top ?? 0) + zone;
    const bottom = r.bottom - (ins.bottom ?? 0) - zone;
    const v = lastY < top ? -Math.min(1, (top - lastY) / zone) : lastY > bottom ? Math.min(1, (lastY - bottom) / zone) : 0;
    if (!v) return;
    const before = scroller.scrollTop;
    scroller.scrollTop += v * 12;
    if (scroller.scrollTop !== before) update();
    raf = requestAnimationFrame(autoScroll);
  }

  const move = (e) => {
    if (press && e.pointerId === press.id) { if (Math.hypot(e.clientX - press.x, e.clientY - press.y) > tol) cancelPress(); return; }
    const mine = drag && e.pointerId === drag.id;
    if (mine || e.pointerId === held) e.stopPropagation(); // the day swipe must not see this finger
    if (!mine) return;
    lastX = e.clientX;
    lastY = e.clientY;
    update();
    if (!raf) raf = requestAnimationFrame(autoScroll);
  };

  const up = (e) => {
    if (press && e.pointerId === press.id) cancelPress();
    if (e.pointerId === held) { held = null; swallowUntil = performance.now() + 350; }
    if (!drag || e.pointerId !== drag.id) return;
    const d = drag;
    drag = null;
    swallowUntil = performance.now() + 350;
    const cancelled = e.type === 'pointercancel';
    if (d.kind === 'edge') {
      if (cancelled || d.value === d.from) { showSel(); return; } // nothing moved: the handles stay
      commitEdge(d);
      return;
    }
    hide();
    if (cancelled) return;
    const draft = newInterval(d);
    if (draft) flows.openAdd(draft);
  };

  function commitEdge(d) {
    const s = sel;
    const day = app.state.day;
    sel = null;
    hide();
    if (!s || !day) return;
    const start = d.edge === 'start' ? d.value : s.startMin;
    const end = d.edge === 'end' ? d.value : s.endMin;
    // Only the moved edge is sent; the range is for the optimistic picture (none for the running entry).
    const range = s.open ? null : { start: minToLocal(day.date, start), end: minToLocal(day.date, end) };
    app.actions.updateEntry(s.id, { [d.edge]: minToLocal(day.date, d.value) }, { range, title: s.title, category: s.category });
  }

  // ----- keeping the page still, and taps -----
  const touchMove = (e) => { if ((held !== null || drag) && e.cancelable) e.preventDefault(); };
  const menu = (e) => { if (press || held !== null || drag) e.preventDefault(); };
  const scrolled = () => cancelPress();
  const click = (e) => {
    if (performance.now() < swallowUntil) { swallowUntil = 0; e.stopPropagation(); e.preventDefault(); return; }
    if (!sel || drag) return;
    if (overlay.contains(e.target)) { e.stopPropagation(); return; }
    cancel(); // a tap anywhere else puts the handles away; on the timeline it does nothing more
    if (scroller.contains(e.target)) { e.stopPropagation(); e.preventDefault(); }
  };
  const key = (e) => { if (e.key === 'Escape' && (sel || drag)) cancel(); };

  // The selected entry follows the day: gone or changed elsewhere → the handles go or move.
  const sync = () => {
    if (!sel && !drag) return;
    const c = ctx();
    if (!c || !(drag?.surface ?? sel.surface).isConnected) { cancel(); return; }
    if (!sel) return;
    const piece = c.day.pieces.find((p) => p.segment?.id === sel.id && !p.future);
    if (!piece) { cancel(); return; }
    if (drag) return;
    Object.assign(sel, entryEdges(piece, c.day, c.limit));
    if (!sel.start && !sel.end) cancel(); else showSel();
  };
  const unsub = app.subscribe(() => { if (sel || drag) Promise.resolve().then(sync); });

  scroller.addEventListener('pointerdown', down, { passive: true });
  scroller.addEventListener('touchmove', touchMove, { passive: false });
  scroller.addEventListener('contextmenu', menu);
  scroller.addEventListener('scroll', scrolled, { passive: true });
  overlay.addEventListener('pointerdown', handleDown);
  window.addEventListener('pointermove', move, true);
  window.addEventListener('pointerup', up, true);
  window.addEventListener('pointercancel', up, true);
  document.addEventListener('click', click, true);
  document.addEventListener('keydown', key);
  scroller.classList.add('ts-long-press');

  return {
    cancel,
    get active() { return !!(sel || drag); },
    destroy() {
      cancel();
      unsub?.();
      overlay.remove();
      scroller.removeEventListener('pointerdown', down);
      scroller.removeEventListener('touchmove', touchMove);
      scroller.removeEventListener('contextmenu', menu);
      scroller.removeEventListener('scroll', scrolled);
      window.removeEventListener('pointermove', move, true);
      window.removeEventListener('pointerup', up, true);
      window.removeEventListener('pointercancel', up, true);
      document.removeEventListener('click', click, true);
      document.removeEventListener('keydown', key);
    },
  };
}
