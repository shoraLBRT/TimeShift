// Design C «Нить» — wiring. All logic lives in /core; this file only connects the screen to it.
import { createApp } from '/core/app.js';
import { createFlows } from '/core/flows.js';
import { configureSheets } from '/core/sheet.js';
import { swipeHorizontal } from '/core/gestures.js';
import { isTyping, prefersReducedMotion } from '/core/dom.js';
import * as T from '/core/time.js';
import { createThread } from './thread.js';
import { createIsland } from './island.js';
import { createHeader } from './header.js';
import { icon, play, EASE } from './util.js';

const $ = (id) => document.getElementById(id);
const main = $('main');
const scroller = $('scroller');
const page = $('page');

const app = createApp({ design: 'c' });
configureSheets({ closeContent: () => icon('close', 20) });
// autoPing off: a ping is answered on the island itself (island.js), not in a sheet.
const flows = createFlows(app, { sheet: { className: 'c-sheet' }, autoPing: false });
flows.renderGate($('gate'));

// ---------- day navigation ----------
let enterDir = 0;      // where the next day comes from: 1 — from the right (a later day), -1 — from the left
let leaving = null;    // the running exit animation

function navigate(date) {
  const s = app.state;
  if (!T.isDate(date) || date === s.viewDate) return;
  enterDir = date > s.viewDate ? 1 : -1;
  app.actions.goToDay(date);
}
const step = (n) => navigate(T.addDays(app.state.viewDate, n));

function onOpened(day, { first }) {
  leaving?.cancel();
  leaving = null;
  page.style.transform = '';
  page.style.opacity = '';
  if (first || prefersReducedMotion()) { play(page, [{ opacity: 0 }, { opacity: 1 }], { duration: 300, easing: 'ease-out' }); return; }
  if (!enterDir) return;
  page.animate([{ opacity: 0, transform: `translate3d(${enterDir * 28}px, 0, 0)` }, { opacity: 1, transform: 'none' }], { duration: 380, easing: EASE });
  enterDir = 0;
}

swipeHorizontal(scroller, {
  mouse: true,
  onProgress(dx, { width }) {
    page.style.transform = `translate3d(${dx}px, 0, 0)`;
    page.style.opacity = String(Math.max(0.2, 1 - Math.abs(dx) / (width * 0.8)));
  },
  onEnd({ direction, dx, width }) {
    const from = { transform: `translate3d(${dx}px, 0, 0)`, opacity: Math.max(0.2, 1 - Math.abs(dx) / (width * 0.8)) };
    page.style.transform = '';
    page.style.opacity = '';
    if (!direction) {
      if (!prefersReducedMotion()) page.animate([from, { transform: 'none', opacity: 1 }], { duration: 280, easing: EASE });
      return;
    }
    if (prefersReducedMotion()) { step(-direction); return; }
    leaving = page.animate([from, { transform: `translate3d(${dx + direction * 48}px, 0, 0)`, opacity: 0 }], { duration: 130, easing: 'ease-in', fill: 'forwards' });
    leaving.finished.then(() => step(-direction), () => {});
  },
});

document.addEventListener('keydown', (e) => {
  if (app.state.phase !== 'ready' || flows.top || isTyping() || e.altKey || e.metaKey || e.ctrlKey) return;
  if (e.key === 'ArrowLeft') step(-1);
  else if (e.key === 'ArrowRight') step(1);
});

// ---------- screen ----------
const thread = createThread(app, flows, { scroller, track: $('track'), head: $('head'), onOpened });
const island = createIsland(app, flows, $('island'));
const header = createHeader(app, flows, { main, navigate });

const THEME_COLOR = { dark: '#0B0B0D', light: '#F7F7F5' };
let theme = null;

app.subscribe((s) => {
  main.hidden = s.phase !== 'ready';
  if (s.themeResolved !== theme) {
    theme = s.themeResolved;
    for (const m of document.querySelectorAll('meta[name="theme-color"]')) m.setAttribute('content', THEME_COLOR[theme] ?? THEME_COLOR.dark);
  }
  if (s.phase !== 'ready') return;
  header.render(s);
  thread.render(s);
  island.render(s);
});
app.start();
