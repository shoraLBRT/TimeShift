// Design C «Нить» — small shared bits: inline icons, rolling characters, motion helpers.
import { prefersReducedMotion } from '/core/dom.js';

const NS = 'http://www.w3.org/2000/svg';
const PATHS = {
  close: 'M6.5 6.5l11 11M17.5 6.5l-11 11',
  plus: 'M12 5.5v13M5.5 12h13',
  arrow: 'M5 12h14M13 6l6 6-6 6',
};

/** Inline stroke icon on a 24 px grid. */
export function icon(name, size = 24) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('class', 'c-icon');
  const p = document.createElementNS(NS, 'path');
  p.setAttribute('d', PATHS[name]);
  svg.append(p);
  return svg;
}

export const EASE = 'cubic-bezier(.22, 1, .36, 1)';

/**
 * Text whose characters change one by one: the old glyph slides out, the new one slides in (transform + opacity).
 * Only the cells that changed are touched, so a ticking timer animates a single digit per second.
 */
export function createRoller(el, { duration = 320, distance = '0.55em' } = {}) {
  const cells = []; // { wrap, cur, ch }
  let first = true;
  const glyph = (ch) => {
    const s = document.createElement('span');
    s.className = 'c-roll-glyph';
    s.textContent = ch;
    return s;
  };
  return {
    /** dir: 1 — new glyph comes from below (forward in time), -1 — from above. */
    set(text, dir = 1) {
      const chars = [...String(text ?? '')];
      const animate = !first && !prefersReducedMotion() && !document.hidden;
      first = false;
      while (cells.length > chars.length) cells.pop().wrap.remove();
      chars.forEach((ch, i) => {
        let c = cells[i];
        if (!c) {
          const wrap = document.createElement('span');
          wrap.className = 'c-roll';
          c = { wrap, cur: glyph(ch), ch };
          wrap.append(c.cur);
          el.append(wrap);
          cells.push(c);
          if (animate) c.cur.animate([{ opacity: 0, transform: `translateY(calc(${distance} * ${dir}))` }, { opacity: 1, transform: 'none' }], { duration, easing: EASE });
          return;
        }
        if (c.ch === ch) return;
        const old = c.cur;
        const next = glyph(ch);
        c.cur = next;
        c.ch = ch;
        if (!animate) { old.replaceWith(next); return; }
        old.classList.add('c-roll-out');
        c.wrap.append(next);
        old.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: `translateY(calc(${distance} * ${-dir}))` }], { duration: duration * 0.7, easing: 'ease-in', fill: 'forwards' })
          .finished.then(() => old.remove(), () => old.remove());
        next.animate([{ opacity: 0, transform: `translateY(calc(${distance} * ${dir}))` }, { opacity: 1, transform: 'none' }], { duration, easing: EASE });
      });
    },
  };
}

/** Run a Web Animation and resolve when it ends (or at once with reduced motion). */
export function play(el, frames, opts) {
  if (prefersReducedMotion()) return Promise.resolve();
  try { return el.animate(frames, opts).finished.catch(() => {}); } catch { return Promise.resolve(); }
}
