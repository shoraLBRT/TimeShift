// Design B — «Сетка»: the iOS Calendar day view. Rendering and wiring only; all logic lives in /core.
import { createApp } from '/core/app.js';
import { createFlows } from '/core/flows.js';
import { configureSheets } from '/core/sheet.js';
import { layoutDay, describePiece, categoryInfo, initialScrollTop } from '/core/timeline.js';
import { h, reconcile, setText, prefersReducedMotion, isTyping } from '/core/dom.js';
import { swipeHorizontal } from '/core/gestures.js';
import { timelineDrag } from '/core/drag.js';
import * as T from '/core/time.js';

const LAYOUT = { hourHeight: 60, minBlockHeight: 14, shortHeight: 34, minHit: 44 };
const GRID_TOP = 12;                         // px above 00:00 inside the scroller (matches .b-grid margin-top)
const EASE = 'cubic-bezier(.32,.72,0,1)';
const LETTERS = ['П', 'В', 'С', 'Ч', 'П', 'С', 'В'];
const MONTHS_IOS = ['янв.', 'февр.', 'мар.', 'апр.', 'мая', 'июн.', 'июл.', 'авг.', 'сент.', 'окт.', 'нояб.', 'дек.'];

configureSheets({ closeContent: () => document.createTextNode('Готово'), closeLabel: 'Готово' });
const app = createApp({ design: 'b' });
const flows = createFlows(app, { sheet: { className: 'b-sheet' } });

const $ = (id) => document.getElementById(id);
const root = $('app');
const header = $('header');
const scroller = $('scroller');
const grid = $('grid');
const hoursEl = $('hours');
const pagerEl = $('pager');
const track = $('track');
const nowLabel = $('nowlabel');
const stripEl = $('strip');
const stripTrack = $('strip-track');

const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
const isReady = () => app.state.phase === 'ready' && !!app.state.viewDate;
const px = (n) => `${Math.round(n * 100) / 100}px`;
const parts = (date) => date.split('-').map(Number);
const reduced = () => prefersReducedMotion();

/** Write a style / attribute / text only when it changed: the minute tick must not touch what stayed the same. */
function put(el, key, value) {
  const c = el.__b ?? (el.__b = {});
  if (c[key] === value) return false;
  c[key] = value;
  return true;
}
const style = (el, prop, value) => { if (put(el, `s:${prop}`, value)) el.style.setProperty(prop, value); };
const attr = (el, name, value) => {
  if (!put(el, `a:${name}`, value ?? null)) return;
  if (value == null || value === false) el.removeAttribute(name); else el.setAttribute(name, value === true ? '' : String(value));
};

// ---------------------------------------------------------------- neighbouring days (preview while paging)

// The core keeps its day cache private, so previews of the neighbouring days come from a small cache of our own.
const peek = new Map();
const inflight = new Set();
function remember(day) {
  if (!day) return;
  peek.delete(day.date);
  peek.set(day.date, day);
  while (peek.size > 12) peek.delete(peek.keys().next().value);
}
async function prefetch(date) {
  const s = app.state;
  if (peek.has(date) || inflight.has(date) || s.phase !== 'ready' || !s.online) return;
  inflight.add(date);
  try {
    const day = await app.api.day(date);
    if (day?.date === date) {
      if (!peek.has(date)) remember(day);
      const side = pager.side.find((v) => v.date === date && !v.day);
      if (side) paintView(side, peek.get(date), app.state);
    }
  } catch { /* a preview is optional */ } finally { inflight.delete(date); }
}
let prefetchTimer = 0;
function schedulePrefetch() {
  clearTimeout(prefetchTimer);
  prefetchTimer = setTimeout(() => {
    const v = app.state.viewDate;
    if (!v) return;
    prefetch(T.addDays(v, -1));
    prefetch(T.addDays(v, 1));
  }, 500);
}

// ---------------------------------------------------------------- a day page

const CHECK = '<svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true"><circle cx="12" cy="12" r="9.25" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M7.9 12.4l2.8 2.8 5.5-6" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>';

function createView(date) {
  const list = h('ol', { class: 'b-day', role: 'list' });
  const nowline = h('div', { class: 'b-nowline', 'aria-hidden': 'true', hidden: true });
  const note = h('div', { class: 'b-day-note', hidden: true });
  const el = h('section', { class: 'b-page', dataset: { date }, 'aria-label': T.fmtDateLabel(date, app.state.today ?? date) }, list, nowline, note);
  return { date, el, list, nowline, note, day: null, me: null, layout: null, slot: 0, painted: false, noteKey: null };
}

function createEvent() {
  const hit = h('button', { type: 'button', class: 'b-ev-hit' });
  const title = h('span', { class: 'b-ev-title' });
  const time = h('span', { class: 'b-ev-time' });
  const ok = h('button', { type: 'button', class: 'b-ev-ok', hidden: true });
  ok.innerHTML = CHECK;
  const body = h('div', { class: 'b-ev-body' }, h('span', { class: 'b-ev-text' }, title, time), h('span', { class: 'b-ev-mark', 'aria-hidden': 'true' }, '?'), ok);
  const face = h('div', { class: 'b-ev-face' }, h('span', { class: 'b-ev-bar' }), body, h('span', { class: 'b-ev-live' }));
  const li = h('li', { class: 'b-ev' }, hit, face);
  li.__r = { hit, title, time, ok };
  return li;
}

function updateEvent(li, x) {
  const { b, d, lanes } = x;
  const r = li.__r;
  li.__piece = b.piece;
  li.__parallel = x.parallel;
  const height = b.height;
  const size = height >= 34 ? 'l' : height >= 22 ? 'm' : height >= 14 ? 's' : 'xs';
  style(li, 'top', px(b.y));
  style(li, 'height', px(Math.max(1, height)));
  attr(li, 'data-kind', b.kind);
  attr(li, 'data-size', size);
  attr(li, 'data-group', d.group);
  attr(li, 'data-cat', d.category.key === 'sleep' ? 'sleep' : null);
  attr(li, 'data-open', d.open ? '' : null);
  attr(li, 'data-unknown', d.unknown ? '' : null);
  attr(li, 'data-track', x.parallel ? 'parallel' : 'main');
  attr(li, 'data-from-edge', b.startMin <= 0 ? '' : null);
  attr(li, 'data-to-edge', x.dayMinutes != null && b.endMin >= x.dayMinutes ? '' : null);
  attr(li, 'data-over', x.covered ? '' : null);
  if (x.parallel) {
    style(li, '--lane', String(b.lane ?? 0));
    style(li, '--lanes', String(lanes));
  }
  style(r.hit, 'top', px(b.hitY - b.y));
  style(r.hit, 'height', px(b.hitHeight));
  if (put(r.hit, 'disabled', d.future)) r.hit.disabled = d.future;
  if (put(r.title, 't', d.title)) r.title.textContent = d.title;
  const time = d.open && b.kind !== 'questionable' ? `с ${d.startText}` : d.rangeText;
  if (put(r.time, 't', time)) r.time.textContent = time;
  attr(r.hit, 'aria-label', `${d.title}, ${d.rangeText}, ${d.statusLabel}${d.open ? ', идёт сейчас' : ''}`);
  const canConfirm = d.actions.confirm && !x.parallel && height >= 30;
  if (put(r.ok, 'hidden', !canConfirm)) r.ok.hidden = !canConfirm;
  if (canConfirm) attr(r.ok, 'aria-label', `Подтвердить: ${d.title}, ${d.rangeText}`);
}

function paintView(view, day, s) {
  if (!day) {
    const key = view === pager.cur && s.dayError ? 'error' : view === pager.cur && s.dayLoading ? 'loading' : 'blank';
    if (view.day) return; // keep what is drawn while the same day reloads
    if (view.noteKey !== key) {
      view.noteKey = key;
      view.note.hidden = key === 'blank';
      view.note.dataset.kind = key;
      if (key === 'error') {
        view.note.replaceChildren(
          h('p', { class: 'b-day-note-text' }, s.online ? 'Не удалось загрузить день.' : 'Нет сети. День загрузится, когда она появится.'),
          h('button', { type: 'button', class: 'b-day-note-button', onClick: () => app.actions.refresh({ full: true }) }, 'Повторить'));
      } else if (key === 'loading') {
        view.note.replaceChildren(...[[9, 150], [11.75, 60], [13, 210], [17.5, 90]].map(([hour, len]) => h('span', { class: 'b-skel', style: { top: px(hour * LAYOUT.hourHeight), height: px(len) } })));
      } else view.note.replaceChildren();
    }
    return;
  }
  if (view.day === day && view.me === s.me) return;
  view.day = day;
  view.me = s.me;
  if (view.noteKey !== 'none') { view.noteKey = 'none'; view.note.hidden = true; view.note.replaceChildren(); }

  const L = layoutDay(day, LAYOUT);
  view.layout = L;
  const lanes = Math.max(1, L.lanes);
  const items = [];
  for (const b of L.blocks) {
    const d = describePiece(b.piece, s.me);
    if (d.unknown && d.future) continue; // free time ahead stays an empty grid
    const covered = L.parallel.some((p) => p.startMin < b.endMin && p.endMin > b.startMin && p.y < b.y + 36);
    items.push({ b, d, parallel: false, lanes, covered, dayMinutes: L.dayMinutes });
  }
  for (const b of L.parallel) items.push({ b, d: describePiece(b.piece, s.me, { parallel: true }), parallel: true, lanes, covered: false, dayMinutes: L.dayMinutes });
  const first = !view.painted;
  reconcile(view.list, items, {
    key: (x) => x.b.key,
    create: () => {
      const li = createEvent();
      if (!first && !reduced()) { li.dataset.enter = ''; setTimeout(() => { delete li.dataset.enter; }, 320); }
      return li;
    },
    update: updateEvent,
    remove: (el, done) => {
      if (reduced()) { done(); return; }
      el.dataset.leave = '';
      setTimeout(done, 200);
    },
  });
  view.painted = true;
  attr(view.list, 'data-lanes', String(Math.min(3, lanes)));
  const hasNow = L.nowY != null;
  if (put(view.nowline, 'hidden', !hasNow)) view.nowline.hidden = !hasNow;
  if (hasNow) style(view.nowline, 'transform', `translate3d(0,${px(L.nowY)},0)`);
}

// ---------------------------------------------------------------- the grid behind the pages (hours, now label)

function syncGrid(s) {
  const L = pager.cur?.layout;
  const dayMinutes = L?.dayMinutes ?? 1440;
  const height = dayMinutes * (LAYOUT.hourHeight / 60);
  style(grid, 'height', px(height));
  const nowMin = L && s.viewDate === s.today && L.nowY != null ? s.day?.nowMin ?? null : null;
  if (put(hoursEl, 'n', dayMinutes)) {
    const hours = [];
    for (let i = 0; i * 60 <= dayMinutes; i++) hours.push({ hour: i, min: i * 60, label: i === 24 ? '00:00' : `${T.pad2(i)}:00` });
    reconcile(hoursEl, hours, {
      key: (x) => `h${x.hour}`,
      create: (x) => { const el = h('div', { class: 'b-hour', style: { top: px(x.min * (LAYOUT.hourHeight / 60)) } }, h('span', { class: 'b-hour-label' }, x.label)); el.__min = x.min; return el; },
    });
  }
  for (const el of hoursEl.children) attr(el, 'data-near', nowMin != null && Math.abs(nowMin - el.__min) < 15 ? '' : null);
  const show = nowMin != null;
  if (put(nowLabel, 'hidden', !show)) nowLabel.hidden = !show;
  if (show) {
    style(nowLabel, 'transform', `translate3d(0,${px(L.nowY)},0)`);
    const text = T.fmtClock(nowMin);
    if (put(nowLabel, 't', text)) nowLabel.textContent = text;
  }
}

const visibleHeight = () => Math.max(120, scroller.clientHeight - header.offsetHeight);
const bottomPad = () => parseFloat(getComputedStyle(scroller, '::after').height) || 0;
function nowScrollTop() {
  const v = pager.cur;
  if (!v?.layout) return null;
  return initialScrollTop(v.layout, visibleHeight(), { day: v.day, offsetTop: GRID_TOP, offsetBottom: bottomPad() });
}
let scrolledOnce = false;
let wantNow = false;
function scrollToNow(smooth) {
  const top = nowScrollTop();
  if (top == null) return;
  if (smooth && !reduced()) scroller.scrollTo({ top, behavior: 'smooth' });
  else scroller.scrollTop = top;
}

// ---------------------------------------------------------------- pager: the day follows the finger

const pager = { cur: null, side: [], x: 0, anim: null, done: null };
const pagerWidth = () => pagerEl.clientWidth || root.clientWidth;
const tx = (x) => `translate3d(${px(x)},0,0)`;
function placeView(view, slot) { view.slot = slot; view.el.style.transform = slot ? `translate3d(${slot * 100}%,0,0)` : ''; }
function setTrackX(x) { pager.x = x; track.style.transform = x ? tx(x) : ''; }
function normalize() {
  for (const v of pager.side) v.el.remove();
  pager.side = [];
  if (pager.cur) placeView(pager.cur, 0);
  setTrackX(0);
  track.style.willChange = '';
}
function flush() {
  if (!pager.anim) return;
  const done = pager.done;
  pager.anim.cancel();
  pager.anim = null;
  pager.done = null;
  done?.();
}
function settle(toX, velocity, then) {
  flush();
  const from = pager.x;
  const dist = Math.abs(toX - from);
  if (dist < 0.5 || reduced() || root.hidden) { setTrackX(toX); then(); return; }
  const ms = clamp(dist / Math.max(Math.abs(velocity), 1.15), 180, 350);
  track.style.willChange = 'transform';
  const anim = track.animate([{ transform: tx(from) }, { transform: tx(toX) }], { duration: ms, easing: EASE });
  setTrackX(toX);
  pager.anim = anim;
  pager.done = then;
  anim.onfinish = () => { if (pager.anim !== anim) return; pager.anim = null; pager.done = null; then(); };
}

swipeHorizontal(scroller, {
  onStart() {
    flush();
    closeMenu();
    if (!pager.cur) return;
    normalize();
    const s = app.state;
    for (const dir of [-1, 1]) {
      const date = T.addDays(pager.cur.date, dir);
      const v = createView(date);
      placeView(v, dir);
      paintView(v, peek.get(date) ?? null, s);
      track.append(v.el);
      pager.side.push(v);
      prefetch(date);
    }
    track.style.willChange = 'transform';
  },
  onProgress(dx) { if (pager.cur) setTrackX(dx); },
  onEnd({ direction, velocity }) {
    if (!pager.cur) return;
    const target = direction ? pager.side.find((v) => v.slot === -direction) : null;
    if (!target) { settle(0, velocity, normalize); return; }
    pager.side = pager.side.filter((v) => v !== target).concat(pager.cur);
    pager.cur = target;
    settle(direction * pagerWidth(), velocity, normalize);
    app.actions.goToDay(target.date);
  },
});

function syncDay(s) {
  if (!pager.cur) {
    pager.cur = createView(s.viewDate);
    track.append(pager.cur.el);
  } else if (pager.cur.date !== s.viewDate) {
    // Changed from outside the swipe: the strip, the title, the summary, midnight.
    flush();
    normalize();
    const dir = s.viewDate > pager.cur.date ? 1 : -1;
    const v = createView(s.viewDate);
    placeView(v, dir);
    track.append(v.el);
    pager.side.push(pager.cur);
    pager.cur = v;
    paintView(v, dayOf(s), s);
    settle(-dir * pagerWidth(), 0, normalize);
  }
  paintView(pager.cur, dayOf(s), s);
}
const dayOf = (s) => (s.day && s.day.date === s.viewDate ? s.day : peek.get(s.viewDate) ?? null);

// Taps on pieces.
track.addEventListener('click', (e) => {
  const li = e.target.closest('.b-ev');
  if (!li || !li.__piece || !pager.cur?.list.contains(li)) return;
  const s = app.state;
  if (e.target.closest('.b-ev-ok')) {
    if (s.busy || !s.day) return;
    const p = li.__piece;
    const end = Math.min(p.endMin, s.day.nowMin ?? p.endMin);
    app.actions.confirm({ start: T.minToLocal(s.day.date, p.startMin), end: T.minToLocal(s.day.date, end) });
    return;
  }
  if (!s.day) return;
  flows.openPiece(li.__piece, { parallel: li.__parallel });
});

// Long press (core/drag.js): handles on a stored entry's edges; on unrecorded time — a new entry, dragged out or,
// without a drag, an hour at that place (as in Calendar).
timelineDrag(app, flows, {
  scroller, surface: () => pager.cur?.el ?? null, layout: () => pager.cur?.layout ?? null,
  ignore: (t) => !!t.closest('.b-ev-ok') || !!t.closest('.b-ev')?.__parallel,
  insets: () => ({ top: header.offsetHeight, bottom: bottomPad() }),
});

// ---------------------------------------------------------------- header: month, week strip, caption

const weeks = [-1, 0, 1].map((slot) => {
  const buttons = LETTERS.map((letter) => h('button', { type: 'button', class: 'b-wd' },
    h('span', { class: 'b-wd-letter', 'aria-hidden': 'true' }, letter), h('span', { class: 'b-wd-num' })));
  const el = h('div', { class: 'b-week', style: { transform: `translate3d(${slot * 100}%,0,0)` } }, buttons);
  if (slot) el.inert = true;
  stripTrack.append(el);
  return { slot, el, buttons, start: null };
});
const strip = { week: null, target: null, x: 0, anim: null, done: null };

function fillWeek(w, start) {
  if (w.start === start) return;
  w.start = start;
  w.buttons.forEach((b, i) => {
    const date = T.addDays(start, i);
    b.dataset.date = date;
    b.lastElementChild.textContent = String(parts(date)[2]);
    b.setAttribute('aria-label', `${T.WEEKDAYS[i]}, ${T.fmtDayMonth(date)}`);
    if (i >= 5) b.dataset.weekend = '';
  });
}
function markWeeks(s) {
  for (const w of weeks) {
    for (const b of w.buttons) {
      const date = b.dataset.date;
      attr(b, 'aria-pressed', date === s.viewDate ? 'true' : 'false');
      attr(b, 'aria-current', date === s.today ? 'date' : null);
      attr(b, 'data-future', date > s.today ? '' : null);
    }
  }
}
function setStripX(x) { strip.x = x; stripTrack.style.transform = x ? tx(x) : ''; }
function flushStrip() {
  if (!strip.anim) return;
  const done = strip.done;
  strip.anim.cancel();
  strip.anim = null;
  strip.done = null;
  done?.();
}
function animateStrip(toX, velocity, then) {
  flushStrip();
  const from = strip.x;
  const dist = Math.abs(toX - from);
  if (dist < 0.5 || reduced() || root.hidden) { setStripX(toX); then(); return; }
  const ms = clamp(dist / Math.max(Math.abs(velocity), 1.15), 180, 350);
  stripTrack.style.willChange = 'transform';
  const anim = stripTrack.animate([{ transform: tx(from) }, { transform: tx(toX) }], { duration: ms, easing: EASE });
  setStripX(toX);
  strip.anim = anim;
  strip.done = then;
  anim.onfinish = () => { if (strip.anim !== anim) return; strip.anim = null; strip.done = null; then(); };
}
function centreStrip(week) {
  strip.week = week;
  strip.target = null;
  for (const w of weeks) fillWeek(w, T.addDays(week, w.slot * 7));
  setStripX(0);
  stripTrack.style.willChange = '';
  markWeeks(app.state);
}
function slideStrip(week, velocity = 0) {
  const from = strip.x; // a finger may have moved it already
  flushStrip();
  setStripX(from);
  const dir = week > strip.week ? 1 : -1;
  fillWeek(weeks.find((w) => w.slot === dir), week);
  strip.target = week;
  markWeeks(app.state);
  animateStrip(-dir * (stripEl.clientWidth || root.clientWidth), velocity, () => centreStrip(week));
}
function syncStrip(s) {
  const week = T.weekStart(s.viewDate);
  if (strip.week == null) centreStrip(week);
  else if (week !== strip.week && week !== strip.target) slideStrip(week);
  markWeeks(s);
}

swipeHorizontal(stripEl, {
  threshold: 0.18,
  onStart() { flushStrip(); stripTrack.style.willChange = 'transform'; },
  onProgress(dx) { setStripX(dx); },
  onEnd({ direction, velocity }) {
    if (!direction || strip.week == null) { animateStrip(0, velocity, () => { stripTrack.style.willChange = ''; }); return; }
    const date = T.addDays(app.state.viewDate, -7 * direction);
    slideStrip(T.weekStart(date), velocity);
    app.actions.goToDay(date);
  },
});

function goToday() {
  const s = app.state;
  wantNow = true;
  if (s.viewDate === s.today) { wantNow = false; scrollToNow(true); } else app.actions.goToday();
}
stripTrack.addEventListener('click', (e) => {
  const b = e.target.closest('.b-wd');
  if (!b) return;
  const s = app.state;
  if (b.dataset.date === s.today) { goToday(); return; }
  app.actions.goToDay(b.dataset.date);
});
$('month').addEventListener('click', goToday);
$('today').addEventListener('click', goToday);
$('add').addEventListener('click', () => { if (isReady()) flows.openAdd(); });

function syncHeader(s) {
  const [y, m, d] = parts(s.viewDate);
  setText($('month-name'), T.capitalize(T.MONTHS[m - 1]));
  setText($('month-year'), y !== parts(s.today)[0] ? String(y) : '');
  const isToday = s.viewDate === s.today;
  attr($('month'), 'aria-label', isToday ? `${T.capitalize(T.MONTHS[m - 1])}. Показать текущее время` : `${T.capitalize(T.MONTHS[m - 1])}. Вернуться к сегодня`);
  const caption = $('caption');
  setText(caption, s.online ? `${T.capitalize(T.WEEKDAYS[T.weekday(s.viewDate) - 1])} — ${d} ${MONTHS_IOS[m - 1]} ${y} г.` : 'Нет сети');
  attr(caption, 'data-offline', s.online ? null : '');
  const today = $('today');
  if (put(today, 'hidden', isToday)) today.hidden = isToday;
}

// ---------------------------------------------------------------- the "now" capsule

const nowEl = $('now');
const nowMain = $('now-main');
const nowStop = $('now-stop');
nowMain.addEventListener('click', () => { if (isReady()) flows.openSwitcher(); });
nowStop.addEventListener('click', () => { if (!app.state.busy) app.actions.stop(); });

function syncNow(s) {
  if (!s.day) return; // keep the last known state while another day loads
  const c = s.day.current;
  attr(nowEl, 'data-running', c ? 'true' : 'false');
  attr(nowEl, 'data-ping', s.ping.pending ? '' : null);
  if (put(nowStop, 'hidden', !c)) nowStop.hidden = !c;
  if (put(nowStop, 'disabled', !!s.busy)) nowStop.disabled = !!s.busy;
  if (!c) {
    setText($('now-title'), 'Чем занят?');
    setText($('now-elapsed'), '');
    attr(nowEl, 'data-group', null);
    attr(nowEl, 'data-cat', null);
    attr(nowEl, 'data-over', null);
    attr(nowMain, 'aria-label', 'Чем занят? Начать занятие');
    return;
  }
  const elapsed = Math.max(0, Math.floor((app.nowMs() - Date.parse(c.startUtc)) / 60000));
  const text = elapsed < 1 ? 'только что' : T.fmtDuration(elapsed);
  setText($('now-title'), c.title);
  setText($('now-elapsed'), text);
  attr(nowEl, 'data-group', categoryInfo(c.category, s.me).group);
  attr(nowEl, 'data-cat', c.category === 'sleep' ? 'sleep' : null);
  attr(nowEl, 'data-over', c.typicalMin && elapsed > c.typicalMin ? '' : null);
  attr(nowMain, 'aria-label', `Сейчас: ${c.title}, ${text}. Сменить занятие`);
  attr(nowStop, 'aria-label', `Закончить: ${c.title}`);
}

// ---------------------------------------------------------------- «⋯» menu

const more = $('more');
const menuLayer = $('menu-layer');
const menu = $('menu');
let menuTimer = 0;
function openMenu(viaKeyboard) {
  clearTimeout(menuTimer);
  menuLayer.hidden = false;
  void menu.offsetWidth;
  menuLayer.dataset.state = 'open';
  more.setAttribute('aria-expanded', 'true');
  if (viaKeyboard) menu.querySelector('.b-menu-item:not([hidden])')?.focus();
}
function closeMenu(focusBack) {
  if (menuLayer.hidden || menuLayer.dataset.state !== 'open') return;
  menuLayer.dataset.state = 'closing';
  more.setAttribute('aria-expanded', 'false');
  if (focusBack) more.focus({ preventScroll: true });
  clearTimeout(menuTimer);
  menuTimer = setTimeout(() => { menuLayer.hidden = true; }, reduced() ? 0 : 180);
}
more.addEventListener('click', (e) => { if (menuLayer.dataset.state === 'open' && !menuLayer.hidden) closeMenu(); else openMenu(e.detail === 0); });
menuLayer.addEventListener('click', (e) => {
  const item = e.target.closest('.b-menu-item');
  if (!item) { closeMenu(); return; }
  closeMenu();
  if (!isReady()) return;
  const flow = item.dataset.flow;
  if (flow === 'summary') flows.openSummary();
  else if (flow === 'assistant') flows.openAssistant();
  else if (flow === 'settings') flows.openSettings();
  else if (flow === 'install') flows.openSettings('other');
});
menu.addEventListener('keydown', (e) => {
  const items = [...menu.querySelectorAll('.b-menu-item:not([hidden])')];
  const i = items.indexOf(document.activeElement);
  if (e.key === 'ArrowDown') { e.preventDefault(); items[(i + 1) % items.length]?.focus(); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); items[(i - 1 + items.length) % items.length]?.focus(); }
  else if (e.key === 'Tab') closeMenu();
});
function syncMenu(s) {
  const kind = s.install?.kind;
  const offer = !s.embed && (kind === 'prompt' || kind === 'ios');
  const item = $('menu-install');
  if (put(item, 'hidden', !offer)) item.hidden = !offer;
  if (offer) setText($('menu-install-text'), kind === 'ios' ? 'На экран «Домой»' : 'Установить приложение');
}

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !menuLayer.hidden) { e.preventDefault(); closeMenu(true); return; }
  if (e.defaultPrevented || e.altKey || e.metaKey || e.ctrlKey || root.inert || root.hidden || isTyping() || !menuLayer.hidden) return;
  if (stripEl.contains(document.activeElement) || menu.contains(document.activeElement)) return;
  if (e.key === 'ArrowLeft') app.actions.prevDay();
  else if (e.key === 'ArrowRight') app.actions.nextDay();
});

// ---------------------------------------------------------------- theme colour of the browser chrome

const themeMetas = [...document.querySelectorAll('meta[name="theme-color"]')];
function syncTheme(s) {
  if (!put(document.documentElement, 'theme', s.themeResolved)) return;
  const color = s.themeResolved === 'dark' ? '#000000' : '#ffffff';
  for (const m of themeMetas) m.setAttribute('content', color);
}

// ---------------------------------------------------------------- wiring

flows.renderGate($('gate'));

let lastBusy = null;
let lastView = null;
app.subscribe((s) => {
  syncTheme(s);
  const ready = s.phase === 'ready' && !!s.viewDate;
  if (root.hidden === ready) root.hidden = !ready;
  if (!ready) return;
  attr(root, 'data-busy', s.busy ?? null);
  attr(root, 'data-today', s.viewDate === s.today ? '' : null);

  remember(s.day);
  // A change may touch the neighbouring days too: previews of them are fetched again.
  if (lastBusy && !s.busy) { for (const k of [...peek.keys()]) if (k !== s.viewDate) peek.delete(k); schedulePrefetch(); }
  lastBusy = s.busy;

  const moved = lastView !== s.viewDate;
  lastView = s.viewDate;
  syncHeader(s);
  syncStrip(s);
  syncDay(s);
  syncGrid(s);
  syncNow(s);
  syncMenu(s);
  if (moved) schedulePrefetch();

  if (pager.cur.layout) {
    if (!scrolledOnce) { scrolledOnce = true; wantNow = false; scrollToNow(false); }
    else if (wantNow && s.viewDate === s.today && pager.cur.day?.nowMin != null) { wantNow = false; scrollToNow(true); }
  }
});

app.start();
