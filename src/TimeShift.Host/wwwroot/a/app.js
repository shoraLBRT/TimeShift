// Design A — «Лист»: a page of a paper day-planner. Rendering only; all logic lives in /core.
import { createApp } from '/core/app.js';
import { createFlows } from '/core/flows.js';
import { configureSheets, openSheet, openSheetCount } from '/core/sheet.js';
import { layoutDay, describePiece, initialScrollTop, scrollTopFor, questionablePieces, pieceBounds, categoryInfo } from '/core/timeline.js';
import { h, reconcile, setText, setData, prefersReducedMotion, isTyping } from '/core/dom.js';
import { swipeHorizontal } from '/core/gestures.js';
import { timelineDrag } from '/core/drag.js';
import * as T from '/core/time.js';

const HOUR = 56;          // px per hour
const PAD_TOP = 18;       // paper above 0:00
const PAD_BOTTOM = 40;    // paper below 24:00
const EASE = 'cubic-bezier(.2, .8, .2, 1)';
const LAYOUT = { hourHeight: HOUR, minBlockHeight: 24, shortHeight: 44, minHit: 44 };

const $ = (id) => document.getElementById(id);
const svg = (markup) => { const t = document.createElement('template'); t.innerHTML = markup.trim(); return t.content.firstChild; };
const ICON = {
  close: '<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><path d="M7 7l10 10M17 7 7 17"/></svg>',
  prev: '<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M14.5 6 8.5 12l6 6"/></svg>',
  next: '<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M9.5 6l6 6-6 6"/></svg>',
};

const app = createApp({ design: 'a' });
configureSheets({ closeContent: () => svg(ICON.close) });
const flows = createFlows(app, { sheet: { className: 'a-sheet' } });
flows.renderGate($('gate'));

const main = $('main');
const head = $('head');
const scroller = $('scroll');
const page = $('page');
const track = $('track');
const hoursEl = $('hours');
const entriesEl = $('entries');
const notesEl = $('notes');
const nowEl = $('now');
const placeholder = $('placeholder');
const nowBtn = $('nowbtn');

/** '09:05' → '9:05' — the way one writes time by hand. */
const clock = (min) => T.fmtClock(min).replace(/^0/, '');

// ---------------------------------------------------------------- header

let headKey = '';
function renderHeader(s) {
  const date = s.viewDate ?? s.today;
  if (!date) return;
  const diff = T.diffDays(s.today, date);
  const near = diff === -1 ? 'вчера' : diff === 1 ? 'завтра' : null;
  const year = date.slice(0, 4) !== s.today.slice(0, 4) ? date.slice(0, 4) : null;
  const weekday = [T.WEEKDAYS[T.weekday(date) - 1], near, year, s.online ? null : 'нет сети'].filter(Boolean).join(' · ');
  const key = `${date}|${weekday}`;
  if (key !== headKey) {
    const dateChanged = headKey && !headKey.startsWith(`${date}|`);
    headKey = key;
    setText($('weekday'), weekday);
    setText($('date-text'), T.fmtDayMonth(date));
    $('date').setAttribute('aria-label', `${T.capitalize(T.fmtDateLong(date, s.today))}. Выбрать другой день`);
    if (dateChanged && !prefersReducedMotion()) {
      $('date-text').animate([{ opacity: 0, transform: 'translate3d(0, 4px, 0)' }, { opacity: 1, transform: 'none' }], { duration: 320, easing: EASE });
    }
  }
  $('today').hidden = diff === 0;

  // One quiet line, only when part of the day is under question.
  const day = s.day;
  let qMin = 0;
  if (day) for (const p of questionablePieces(day)) qMin += Math.min(p.endMin, day.nowMin ?? p.endMin) - p.startMin;
  const attn = $('attn');
  attn.hidden = qMin <= 0;
  if (qMin > 0) setText(attn, `Под вопросом ${T.fmtDuration(qMin)}`);
}

// Keep the page still when the header changes height (the quiet line comes and goes).
let headH = 0;
new ResizeObserver(() => {
  const hNow = head.offsetHeight;
  if (headH && hNow !== headH && !main.hidden) scroller.scrollTop += hNow - headH;
  headH = hNow;
}).observe(head);

scroller.addEventListener('scroll', () => {
  const on = scroller.scrollTop > 2;
  if ((head.dataset.scrolled === 'true') !== on) head.dataset.scrolled = String(on);
}, { passive: true });

$('attn').addEventListener('click', () => {
  const day = app.state.day;
  const first = day && questionablePieces(day)[0];
  if (!first || !layout) return;
  const top = scrollTopFor(layout, first.startMin, scroller.clientHeight, { ratio: 0.28, offsetTop: PAD_TOP });
  scroller.scrollTo({ top, behavior: prefersReducedMotion() ? 'auto' : 'smooth' });
  const el = [...entriesEl.children].find((x) => x.__b?.piece === first);
  if (el && !prefersReducedMotion()) el._.body.animate([{ opacity: 1 }, { opacity: 0.35 }, { opacity: 1 }], { duration: 900, delay: 260, easing: 'ease-in-out' });
});

$('today').addEventListener('click', () => {
  const s = app.state;
  if (s.viewDate === s.today) return;
  turn(s.viewDate > s.today ? 1 : -1, () => app.actions.goToday());
});
$('date').addEventListener('click', () => openCalendar());

// ---------------------------------------------------------------- the «⋯» menu

const menuLayer = $('menu-layer');
const menu = $('menu');
const moreBtn = $('more');
let menuTimer = 0;
function openMenu() {
  clearTimeout(menuTimer);
  const s = app.state;
  menu.querySelector('[data-act="install"]').hidden = s.embed || !(s.install?.kind === 'prompt' || s.install?.kind === 'ios');
  menuLayer.hidden = false;
  moreBtn.setAttribute('aria-expanded', 'true');
  void menu.offsetWidth;
  menuLayer.dataset.state = 'open';
  menu.querySelector('.a-menu-item:not([hidden])').focus({ preventScroll: true });
}
function closeMenu({ focus = true } = {}) {
  if (menuLayer.hidden) return;
  menuLayer.dataset.state = 'closing';
  moreBtn.setAttribute('aria-expanded', 'false');
  if (focus) moreBtn.focus({ preventScroll: true });
  menuTimer = setTimeout(() => { menuLayer.hidden = true; }, 180);
}
moreBtn.addEventListener('click', () => (menuLayer.hidden || menuLayer.dataset.state === 'closing' ? openMenu() : closeMenu()));
menuLayer.addEventListener('click', (e) => {
  const item = e.target.closest('.a-menu-item');
  closeMenu({ focus: !item });
  if (!item) return;
  if (item.dataset.act === 'summary') flows.openSummary();
  else if (item.dataset.act === 'settings') flows.openSettings();
  else if (item.dataset.act === 'install') flows.openSettings('other');
});
menuLayer.addEventListener('keydown', (e) => {
  const items = [...menu.querySelectorAll('.a-menu-item:not([hidden])')];
  const i = items.indexOf(document.activeElement);
  if (e.key === 'Escape') { e.preventDefault(); closeMenu(); }
  else if (e.key === 'ArrowDown') { e.preventDefault(); items[(i + 1) % items.length].focus(); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); items[(i - 1 + items.length) % items.length].focus(); }
  else if (e.key === 'Tab') { e.preventDefault(); closeMenu(); }
});

// ---------------------------------------------------------------- calendar (jump to any day)

function openCalendar() {
  const s0 = app.state;
  let month = `${(s0.viewDate ?? s0.today).slice(0, 7)}-01`;
  const grid = h('div', { class: 'a-cal-grid', role: 'grid' });
  const week = h('div', { class: 'a-cal-week', 'aria-hidden': 'true' }, T.WEEKDAYS_SHORT.map((w) => h('span', null, w)));
  const el = h('div', { class: 'a-cal' }, week, grid);
  const shift = (n) => {
    const p = T.dateParts(month);
    const m = p.month - 1 + n;
    month = `${p.year + Math.floor(m / 12)}-${T.pad2(((m % 12) + 12) % 12 + 1)}-01`;
    draw(n);
  };
  const prev = h('button', { type: 'button', class: 'a-cal-nav', 'aria-label': 'Предыдущий месяц', onClick: () => shift(-1) }, svg(ICON.prev));
  const next = h('button', { type: 'button', class: 'a-cal-nav', 'aria-label': 'Следующий месяц', onClick: () => shift(1) }, svg(ICON.next));
  const sheet = openSheet({ content: el, title: T.fmtMonthYear(month), className: 'a-sheet a-sheet--cal', detent: 'auto' });
  sheet.header.insertBefore(h('div', { class: 'a-cal-navs' }, prev, next), sheet.header.querySelector('.ts-sheet-close'));

  function draw(dir = 0) {
    const s = app.state;
    sheet.setTitle(T.fmtMonthYear(month));
    const cells = [];
    for (let i = 1; i < T.weekday(month); i++) cells.push(h('span', { class: 'a-cal-blank' }));
    for (let d = month; d.slice(0, 7) === month.slice(0, 7); d = T.addDays(d, 1)) {
      const date = d;
      cells.push(h('button', {
        type: 'button', class: 'a-cal-day', role: 'gridcell',
        dataset: { today: date === s.today ? 'true' : null, selected: date === s.viewDate ? 'true' : null, future: date > s.today ? 'true' : null },
        'aria-label': T.capitalize(T.fmtDateLong(date, s.today)), 'aria-current': date === s.viewDate ? 'date' : null,
        onClick: () => {
          sheet.close('done');
          const cur = app.state.viewDate;
          if (date !== cur) turn(date < cur ? 1 : -1, () => app.actions.goToDay(date));
        },
      }, h('span', null, String(T.dateParts(date).day))));
    }
    grid.replaceChildren(...cells);
    if (dir && !prefersReducedMotion()) grid.animate([{ opacity: 0, transform: `translate3d(${dir * 16}px, 0, 0)` }, { opacity: 1, transform: 'none' }], { duration: 280, easing: EASE });
  }
  draw();
}

// ---------------------------------------------------------------- timeline

let layout = null;

function createItem() {
  const btn = h('button', { type: 'button', class: 'a-entry' });
  const title = h('span', { class: 'a-title' });
  const sub = h('span', { class: 'a-sub' });
  const dur = h('span', { class: 'a-dur' });
  const mark = h('span', { class: 'a-q' }, '?');
  const row = h('div', { class: 'a-row' }, h('span', { class: 'a-lines', 'aria-hidden': 'true' }, title, sub), h('span', { class: 'a-side', 'aria-hidden': 'true' }, dur, mark));
  const body = h('div', { class: 'a-body' }, h('span', { class: 'a-spine', 'aria-hidden': 'true' }), row);
  const el = h('div', { class: 'a-item', role: 'listitem' }, btn, body);
  el._ = { btn, body, row, title, sub, dur, ring: null, top: null, height: null, bodyTop: null, bodyHeight: null };
  return el;
}

const px = (el, prop, cacheKey, value) => {
  const v = Math.round(value * 100) / 100;
  if (el._[cacheKey] !== v) { el._[cacheKey] = v; (cacheKey.startsWith('body') ? el._.body : el).style[prop] = `${v}px`; }
};

function updateItem(el, b, s) {
  const d = b.d;
  const r = el._;
  const was = el.dataset.kind;
  el.__b = b;
  px(el, 'top', 'top', b.hitY);
  px(el, 'height', 'height', b.hitHeight);
  px(el, 'top', 'bodyTop', b.y - b.hitY);
  px(el, 'height', 'bodyHeight', b.height);

  const size = b.height >= 44 ? 'full' : b.height >= 24 ? 'line' : b.height >= 16 ? 'mini' : 'none';
  const sleep = d.category.key === 'sleep' || d.category.parent === 'sleep';
  setData(el, 'kind', b.kind);
  setData(el, 'group', d.group);
  setData(el, 'cat', sleep ? 'sleep' : null);
  setData(el, 'size', size);
  setData(el, 'open', d.open ? 'true' : null);
  setData(el, 'unknown', d.unknown ? 'true' : null);
  setData(el, 'par', b.beside ? 'true' : null);
  setData(el, 'still', b.still ? 'true' : null);
  const inset = size === 'line' ? Math.max(0, Math.min(3, Math.floor((b.height - 22) / 2))) : null;
  if (r.inset !== inset) { r.inset = inset; if (inset == null) r.row.style.removeProperty('padding-top'); else r.row.style.paddingTop = `${inset}px`; }

  // Stored segments know their real bounds (a night that began yesterday): write those.
  let startMin = d.startMin;
  let endMin = d.endMin;
  if (b.piece.segment?.startLocal && s.day) {
    const pb = pieceBounds(b.piece, s.day);
    if (pb.startMin < 0) startMin = pb.startMin;
    if (pb.endMin != null && pb.endMin > s.day.dayMinutes) endMin = pb.endMin;
  }
  const dur = T.fmtDuration(Math.max(0, endMin - startMin));
  const range = `${clock(startMin)} – ${d.open ? 'сейчас' : clock(endMin)}`;
  setText(r.title, d.title);
  setText(r.sub, size === 'full' ? `${range} · ${dur}` : '');
  setText(r.dur, size === 'line' ? dur : '');
  r.btn.disabled = d.future;
  const label = [d.title, `${T.fmtClock(startMin)}–${d.open ? 'сейчас' : T.fmtClock(endMin)}`, dur, d.statusLabel, d.open ? 'идёт сейчас' : null].filter(Boolean).join(', ');
  if (r.label !== label) { r.label = label; r.btn.setAttribute('aria-label', label); }

  // The confirm ring of a pencilled-in piece.
  const wantRing = d.actions.confirm && (size === 'full' || size === 'line');
  if (wantRing && !r.ring) {
    r.ring = h('button', { type: 'button', class: 'a-ring' }, h('span', { class: 'a-ring-o', 'aria-hidden': 'true' }));
    r.row.append(r.ring);
  }
  if (r.ring) {
    if (wantRing) {
      r.ring.setAttribute('aria-label', `Подтвердить: ${d.title}, ${T.fmtClock(startMin)}–${T.fmtClock(endMin)}`);
      if (!s.busy && r.ring.dataset.filling) delete r.ring.dataset.filling;
    } else {
      const ring = r.ring;
      r.ring = null;
      if (ring.dataset.filling && !prefersReducedMotion()) {
        ring.disabled = true;
        ring.animate([{ opacity: 1, transform: 'scale(1)' }, { opacity: 0, transform: 'scale(.6)' }], { duration: 260, delay: 120, easing: EASE, fill: 'forwards' }).finished.then(() => ring.remove(), () => ring.remove());
      } else ring.remove();
    }
  }
  if (was === 'assumed' && b.kind === 'confirmed' && !prefersReducedMotion()) {
    r.body.animate([{ opacity: 0.55 }, { opacity: 1 }], { duration: 360, easing: EASE });
  }
}

function createNote() {
  const btn = h('button', { type: 'button', class: 'a-note-btn' });
  const label = h('span', { class: 'a-note-label', 'aria-hidden': 'true' });
  const body = h('div', { class: 'a-note-body' }, label, h('span', { class: 'a-spine', 'aria-hidden': 'true' }));
  const el = h('div', { class: 'a-note', role: 'listitem' }, btn, body);
  el._ = { btn, body, label };
  return el;
}

function updateNote(el, b) {
  const d = b.d;
  el.__b = b;
  el.style.top = `${b.hitY}px`;
  el.style.height = `${b.hitHeight}px`;
  el._.body.style.top = `${b.y - b.hitY}px`;
  el._.body.style.height = `${Math.max(3, b.height)}px`;
  el.style.setProperty('--lane', String(b.lane ?? 0));
  setData(el, 'group', d.group);
  setData(el, 'kind', b.kind);
  setText(el._.label, d.title);
  el._.btn.disabled = d.future;
  el._.btn.setAttribute('aria-label', `Параллельно: ${d.title}, ${d.rangeText}, ${d.durationText}`);
}

// The rest of a template block after "now" starts a minute later every minute: key it by its end instead,
// so the node lives on (the core's key contains the moving start).
const stableKey = (b, day) => (b.piece.future && !b.piece.segment && day?.nowMin != null && b.piece.startMin === day.nowMin
  ? `rest:${b.piece.source}:${b.piece.category ?? ''}:${b.piece.title}:${b.piece.endMin}` : b.key);

let scrolledFor = null;
let enterDir = null;     // a day turn is waiting for the new leaf
let enterTimer = 0;

function renderTimeline(s) {
  const day = s.day;
  const L = layoutDay(day, LAYOUT);
  layout = L;
  if (track.__h !== L.height) { track.__h = L.height; track.style.height = `${L.height}px`; }

  // Loading / failed day: calm pencil strokes on ruled paper.
  const state = day ? null : s.dayError ? 'error' : 'loading';
  if (placeholder.dataset.state !== (state ?? '')) {
    placeholder.dataset.state = state ?? '';
    placeholder.hidden = !state;
    placeholder.replaceChildren();
    if (state === 'error') {
      placeholder.append(h('p', { class: 'a-placeholder-text' }, s.online ? 'Не получилось открыть этот день.' : 'Нет сети — этот день пока не открыть.'),
        h('button', { type: 'button', class: 'a-textbtn', onClick: () => app.actions.refresh({ full: true }) }, 'Повторить'));
    } else if (state === 'loading') {
      placeholder.append(...[[8.5, 3.5], [12.25, 1], [13.5, 4.5], [19, 2]].map(([at, len], i) => h('span', { class: 'a-stroke', style: { top: `${at * HOUR}px`, height: `${len * HOUR - 2}px`, '--i': String(i) } })));
    }
  }

  const nowY = L.nowY;
  reconcile(hoursEl, L.hours.filter((x) => x.min < L.dayMinutes), {
    key: (x) => `h${x.hour}`,
    create: (x) => h('span', { class: 'a-hour', style: { top: `${x.y}px` } }, String(x.hour)),
    update: (el, x) => setData(el, 'near', nowY != null && Math.abs(x.y - nowY) < 15 ? 'true' : null),
  });

  const par = L.parallel;
  const blocks = L.blocks
    .filter((b) => b.height > 0 && !(b.piece.future && b.piece.source === 'background'))
    .map((b) => ({
      ...b, key: stableKey(b, day), d: describePiece(b.piece, s.me),
      still: par.some((p) => p.startMin < b.endMin && p.endMin > b.startMin),          // a margin note alongside: the title does not travel
      beside: par.some((p) => p.y < b.y + 46 && p.y + Math.max(p.height, 20) > b.y),   // …and it sits right next to the title
    }));
  reconcile(entriesEl, blocks, { key: (b) => b.key, create: createItem, update: (el, b) => updateItem(el, b, s) });
  reconcile(notesEl, par.map((b) => ({ ...b, d: describePiece(b.piece, s.me, { parallel: true }) })), { key: (b) => b.key, create: createNote, update: updateNote });

  // The ribbon.
  nowEl.hidden = nowY == null;
  if (nowY != null) {
    if (nowEl.__y !== nowY) { nowEl.__y = nowY; nowEl.style.transform = `translate3d(0, ${nowY}px, 0)`; }
    setText($('now-time'), clock(day.nowMin));
  }

  // On opening a day: "now" at 40 % of the viewport (other days: the morning).
  const key = day ? day.date : `…${s.viewDate}`;
  if (scrolledFor !== key) {
    scrolledFor = key;
    scroller.scrollTop = initialScrollTop(L, scroller.clientHeight, { day, offsetTop: PAD_TOP, offsetBottom: PAD_BOTTOM });
    headH = head.offsetHeight;
    if (enterDir != null) playEnter();
    else if (day) playStagger();
  }
}

/** First paint of a day: the lines visible on screen fade up, one after another. */
function playStagger() {
  if (prefersReducedMotion()) return;
  const top = scroller.scrollTop - PAD_TOP;
  const bottom = top + scroller.clientHeight;
  let i = 0;
  for (const el of entriesEl.children) {
    const b = el.__b;
    if (!b || b.y + b.height < top || b.y > bottom) continue;
    if (i >= 12) break;
    el.animate([{ opacity: 0, transform: 'translate3d(0, 6px, 0)' }, { opacity: 1, transform: 'none' }], { duration: 340, delay: i * 16, easing: EASE, fill: 'backwards' });
    i += 1;
  }
}

// Taps: the ring confirms on the spot, everything else opens the right panel.
entriesEl.addEventListener('click', (e) => {
  const item = e.target.closest('.a-item');
  const b = item?.__b;
  if (!b) return;
  const ring = e.target.closest('.a-ring');
  if (ring) { confirmHere(ring, b); return; }
  flows.openPiece(b.piece);
});
notesEl.addEventListener('click', (e) => {
  const b = e.target.closest('.a-note')?.__b;
  if (b) flows.openPiece(b.piece, { parallel: true });
});

function confirmHere(ring, b) {
  const s = app.state;
  if (s.busy || ring.dataset.filling || !s.day) return;
  ring.dataset.filling = 'true';
  const date = s.day.date;
  const end = Math.min(b.piece.endMin, s.day.nowMin ?? b.piece.endMin);
  const go = () => app.actions.confirm({ start: T.minToLocal(date, b.piece.startMin), end: T.minToLocal(date, end) });
  if (prefersReducedMotion()) go(); else setTimeout(go, 180);
}

// ---------------------------------------------------------------- turning days

let turning = false;

function playEnter() {
  clearTimeout(enterTimer);
  const dir = enterDir;
  enterDir = null;
  page.style.opacity = '';
  page.style.transform = '';
  let settled = false;
  const done = () => { if (settled) return; settled = true; page.style.willChange = ''; turning = false; };
  if (dir == null) { done(); return; }
  const from = prefersReducedMotion() ? 'none' : `translate3d(${-dir * 24}px, 0, 0)`;
  page.animate([{ transform: from, opacity: 0 }, { transform: 'none', opacity: 1 }], { duration: 280, easing: EASE }).finished.then(done, done);
  setTimeout(done, 360);   // a hidden page draws no frames: never wait for them
}

/**
 * Turn the leaf: the current page slides a little further and fades, the day changes, the new one settles in.
 * dir: 1 — to an earlier day (the page leaves to the right), -1 — to a later one.
 */
function turn(dir, go, fromDx = 0) {
  if (turning) return;
  turning = true;
  page.style.willChange = 'transform, opacity';
  const rm = prefersReducedMotion();
  const o0 = Number(getComputedStyle(page).opacity) || 1;
  const out = page.animate(
    [{ transform: `translate3d(${fromDx}px, 0, 0)`, opacity: o0 }, { transform: `translate3d(${rm ? 0 : fromDx + dir * 24}px, 0, 0)`, opacity: 0 }],
    { duration: 140, easing: 'ease-out', fill: 'forwards' });
  let left = false;
  const after = () => {
    if (left) return;
    left = true;
    page.style.transform = '';
    page.style.opacity = '0';
    out.cancel();
    enterDir = dir;
    const before = app.state.viewDate;
    go();
    // The new day normally arrives with the next render; if nothing changed, settle anyway.
    enterTimer = setTimeout(() => { if (enterDir != null) { if (app.state.viewDate === before) scrolledFor = null; playEnter(); } }, 120);
  };
  out.finished.then(after, after);
  setTimeout(after, 220);
}

swipeHorizontal(scroller, {
  mouse: true,
  onStart() { if (!turning) page.style.willChange = 'transform, opacity'; },
  onProgress(dx, { width }) {
    if (turning) return;
    page.style.transform = `translate3d(${dx}px, 0, 0)`;
    page.style.opacity = String(1 - Math.min(1, Math.abs(dx) / width) * 0.8);
  },
  onEnd({ direction, dx }) {
    if (turning) return;
    if (direction) { turn(direction, () => (direction === 1 ? app.actions.prevDay() : app.actions.nextDay()), dx); return; }
    const o0 = Number(page.style.opacity) || 1;
    page.style.transform = '';
    page.style.opacity = '';
    const back = page.animate([{ transform: `translate3d(${dx}px, 0, 0)`, opacity: o0 }, { transform: 'none', opacity: 1 }], { duration: 280, easing: EASE });
    const done = () => { page.style.willChange = ''; };
    back.finished.then(done, done);
  },
});

document.addEventListener('keydown', (e) => {
  if (main.hidden || e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || isTyping() || openSheetCount() || !menuLayer.hidden) return;
  if (e.key === 'ArrowLeft') turn(1, () => app.actions.prevDay());
  else if (e.key === 'ArrowRight') turn(-1, () => app.actions.nextDay());
});

// ---------------------------------------------------------------- the bottom bar

let todayCurrent = null;
function renderBar(s) {
  const day = s.day;
  if (day && day.date === s.today) todayCurrent = day.current ?? null;
  const cur = day && day.date === s.today ? day.current : day?.current ?? todayCurrent;
  const running = !!cur;
  setData(nowBtn, 'running', String(running));
  setData(nowBtn, 'ping', s.ping?.pending ? 'true' : null);
  if (running) {
    const elapsed = day && day.date === s.today && day.current ? cur.elapsedMin : Math.max(0, Math.floor((app.nowMs() - Date.parse(cur.startUtc)) / 60000));
    setData(nowBtn, 'group', categoryInfo(cur.category, s.me).group);
    setData(nowBtn, 'over', cur.typicalMin && elapsed > cur.typicalMin ? 'true' : null);
    setText($('nowbtn-title'), cur.title);
    setText($('nowbtn-elapsed'), T.fmtCompact(elapsed));
    nowBtn.setAttribute('aria-label', `Сейчас: ${cur.title}, ${T.fmtDuration(elapsed)}. Сменить или закончить`);
  } else {
    setData(nowBtn, 'group', null);
    setData(nowBtn, 'over', null);
    setText($('nowbtn-title'), 'Чем занят?');
    setText($('nowbtn-elapsed'), '');
    nowBtn.setAttribute('aria-label', 'Чем занят? Начать занятие');
  }
}
nowBtn.addEventListener('click', () => flows.openSwitcher());
$('pen').addEventListener('click', () => flows.openAdd());

// Long press: handles on a stored entry's edges, or a new entry dragged out over unrecorded time (core/drag.js).
timelineDrag(app, flows, {
  scroller, surface: $('track'), layout: () => layout, ignore: '.a-note, .a-ring',
  insets: () => ({ top: head.offsetHeight, bottom: $('bar')?.offsetHeight ?? 0 }),
});

// ---------------------------------------------------------------- render loop

let last = {};
function render(s) {
  const ready = s.phase === 'ready';
  if (main.hidden === ready) main.hidden = !ready;
  if (s.themeResolved !== last.themeResolved) {
    // The browser chrome follows the paper, also when the theme is chosen by hand.
    const paper = s.themeResolved === 'dark' ? '#161513' : '#F4EFE6';
    for (const m of document.querySelectorAll('meta[name="theme-color"]')) m.setAttribute('content', paper);
  }
  if (!ready) { closeMenu({ focus: false }); last = s; return; }
  renderHeader(s);
  renderBar(s);
  if (s.day !== last.day || s.me !== last.me || s.busy !== last.busy || s.dayLoading !== last.dayLoading || s.dayError !== last.dayError
    || s.viewDate !== last.viewDate || s.online !== last.online || last.phase !== 'ready') renderTimeline(s);
  last = s;
}
app.subscribe(render);
app.start();
