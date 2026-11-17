// Gestures: horizontal swipe for day navigation (never fights vertical scrolling) and long press.

/**
 * Horizontal swipe recogniser. Give the element `touch-action: pan-y` (base.css: `.ts-swipe-area`) so the browser
 * keeps vertical scrolling and hands horizontal moves to us.
 * @param {Element} el
 * @param {object} o
 * @param {(info) => void} [o.onStart]     the gesture locked horizontally
 * @param {(dx: number, info) => void} [o.onProgress]  finger offset in px (designs move the page with it)
 * @param {(result: { direction: -1|0|1, dx, velocity }) => void} o.onEnd
 *        direction 1 = swipe right (go to the previous day), -1 = swipe left (next day), 0 = cancelled
 * @param {number} [o.threshold=0.22]  fraction of the width that commits the swipe
 * @param {number} [o.velocity=0.35]   px/ms that commits a short flick
 * @param {number} [o.edge=16]         ignore touches starting this close to the left edge (iOS back gesture)
 * @param {(dir) => boolean} [o.canSwipe] return false to resist a direction (e.g. no next day); rubber-banded
 * @param {boolean} [o.mouse=false]    also react to mouse drags (desktop development)
 * @returns {{ destroy() }}
 */
export function swipeHorizontal(el, o) {
  const threshold = o.threshold ?? 0.22;
  const vMin = o.velocity ?? 0.35;
  const edge = o.edge ?? 16;
  let g = null;

  const down = (e) => {
    if (g || (e.pointerType === 'mouse' && (!o.mouse || e.button !== 0))) return;
    if (e.clientX < edge || e.target.closest('[data-ts-noswipe],input,textarea,select,.ts-sheet')) return;
    for (let n = e.target; n && n !== el; n = n.parentElement) {
      if (n.scrollWidth > n.clientWidth + 1 && /(auto|scroll)/.test(getComputedStyle(n).overflowX)) return;
    }
    g = { id: e.pointerId, x0: e.clientX, y0: e.clientY, mode: 'pending', samples: [[e.timeStamp, e.clientX]], dx: 0, width: el.clientWidth || window.innerWidth };
  };
  const move = (e) => {
    if (!g || e.pointerId !== g.id) return;
    const dx = e.clientX - g.x0;
    const dy = e.clientY - g.y0;
    if (g.mode === 'pending') {
      if (Math.abs(dx) < 10 && Math.abs(dy) < 10) return;
      if (Math.abs(dy) >= Math.abs(dx) * 0.8) { g = null; return; } // vertical: leave it to scrolling
      g.mode = 'swipe';
      g.x0 = e.clientX; // start from here: no jump
      try { el.setPointerCapture(e.pointerId); } catch { /* ignore */ }
      o.onStart?.({ width: g.width });
    }
    let d = e.clientX - g.x0;
    const dir = d > 0 ? 1 : -1;
    if (d && o.canSwipe && !o.canSwipe(dir)) d = dir * Math.sqrt(Math.abs(d)) * 4; // resist
    g.dx = d;
    g.samples.push([e.timeStamp, e.clientX]);
    while (g.samples.length > 2 && e.timeStamp - g.samples[0][0] > 100) g.samples.shift();
    o.onProgress?.(d, { width: g.width, progress: d / g.width });
  };
  const up = (e) => {
    if (!g || e.pointerId !== g.id) return;
    const s = g;
    g = null;
    if (s.mode !== 'swipe') return;
    const [t0, x0] = s.samples[0];
    const [t1, x1] = s.samples.at(-1);
    const v = t1 > t0 ? (x1 - x0) / (t1 - t0) : 0;
    let direction = 0;
    if (e.type !== 'pointercancel' && (Math.abs(s.dx) > s.width * threshold || (Math.abs(v) > vMin && Math.abs(s.dx) > 24 && Math.sign(v) === Math.sign(s.dx)))) direction = s.dx > 0 ? 1 : -1;
    if (direction && o.canSwipe && !o.canSwipe(direction)) direction = 0;
    o.onEnd({ direction, dx: s.dx, velocity: v, width: s.width });
  };
  // A swipe must not also act as a tap on what is under the finger.
  const click = (e) => { if (suppressUntil > performance.now()) { e.stopPropagation(); e.preventDefault(); } };
  let suppressUntil = 0;
  const upWrap = (e) => { const was = g?.mode === 'swipe'; up(e); if (was) suppressUntil = performance.now() + 350; };

  el.addEventListener('pointerdown', down, { passive: true });
  el.addEventListener('pointermove', move, { passive: true });
  el.addEventListener('pointerup', upWrap, { passive: true });
  el.addEventListener('pointercancel', up, { passive: true });
  el.addEventListener('click', click, true);
  el.classList.add('ts-swipe-area');
  return {
    destroy() {
      el.removeEventListener('pointerdown', down);
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', upWrap);
      el.removeEventListener('pointercancel', up);
      el.removeEventListener('click', click, true);
    },
  };
}

/**
 * Long press. Fires once after `ms` without moving more than `tolerance` px; the click that follows is swallowed.
 * @param {Element} el
 * @param {(e: PointerEvent) => void} onLongPress
 * @param {{ ms?: number, tolerance?: number, selector?: string }} [o]  selector: delegate to matching descendants
 * @returns {{ destroy() }}
 */
export function longPress(el, onLongPress, o = {}) {
  const ms = o.ms ?? 450;
  const tol = o.tolerance ?? 10;
  let t = null;
  let fired = false;
  const cancel = () => { if (t) { clearTimeout(t.timer); t = null; } };
  const down = (e) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    const target = o.selector ? e.target.closest(o.selector) : el;
    if (!target || !el.contains(target)) return;
    fired = false;
    t = { x: e.clientX, y: e.clientY, timer: setTimeout(() => { fired = true; t = null; navigator.vibrate?.(10); onLongPress(e, target); }, ms) };
  };
  const move = (e) => { if (t && Math.hypot(e.clientX - t.x, e.clientY - t.y) > tol) cancel(); };
  const click = (e) => { if (fired) { fired = false; e.stopPropagation(); e.preventDefault(); } };
  const menu = (e) => { if (fired || t) e.preventDefault(); };
  el.addEventListener('pointerdown', down, { passive: true });
  el.addEventListener('pointermove', move, { passive: true });
  el.addEventListener('pointerup', cancel, { passive: true });
  el.addEventListener('pointercancel', cancel, { passive: true });
  el.addEventListener('click', click, true);
  el.addEventListener('contextmenu', menu);
  el.classList.add('ts-long-press');
  return {
    destroy() {
      cancel();
      el.removeEventListener('pointerdown', down);
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', cancel);
      el.removeEventListener('pointercancel', cancel);
      el.removeEventListener('click', click, true);
      el.removeEventListener('contextmenu', menu);
    },
  };
}
