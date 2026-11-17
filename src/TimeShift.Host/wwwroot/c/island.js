// Design C «Нить» — the island: what is running and for how long, and the one-tap answer to a ping, in place.
import { h, setText, setData } from '/core/dom.js';
import { categoryInfo } from '/core/timeline.js';
import * as T from '/core/time.js';
import { createRoller, icon } from './util.js';

const GROW_RUNNING = 60;  // px the island grows by for «Продолжаю / Закончил»
const GROW_IDLE = 58;     // … and for the row of recent activities

export function createIsland(app, flows, root) {
  const mark = h('span', { class: 'c-island-mark', 'aria-hidden': 'true' });
  const title = h('span', { class: 'c-island-title' });
  const question = h('span', { class: 'c-island-question' });
  const tMain = h('span', { class: 'c-timer-main' });
  const tSec = h('span', { class: 'c-timer-sec' });
  const timer = h('span', { class: 'c-island-timer', 'aria-hidden': 'true' }, tMain, tSec);
  const main = h('button', { type: 'button', class: 'c-island-main' }, mark, h('span', { class: 'c-island-titles', 'aria-hidden': 'true' }, title, question), timer);
  const add = h('button', { type: 'button', class: 'c-island-add' }, icon('plus'));
  const yes = h('button', { type: 'button', class: 'c-ping-btn', dataset: { variant: 'primary' } }, 'Продолжаю');
  const no = h('button', { type: 'button', class: 'c-ping-btn', dataset: { variant: 'secondary' } }, 'Закончил');
  const other = h('button', { type: 'button', class: 'c-ping-other' }, 'Другое');
  const answer = h('div', { class: 'c-ping-answer' }, yes, no, other);
  const chips = h('div', { class: 'c-ping-chips', 'data-ts-noswipe': '' });
  const ping = h('div', { class: 'c-island-ping' }, answer, chips);
  ping.inert = true;
  root.append(
    h('div', { class: 'c-island-shadow', 'aria-hidden': 'true' }),
    h('div', { class: 'c-island-bg', 'aria-hidden': 'true' }, h('i', { class: 'c-cap-top' }), h('i', { class: 'c-cap-mid' }), h('i', { class: 'c-cap-bot' })),
    h('div', { class: 'c-island-row' }, main, add),
    ping);

  const mainRoll = createRoller(tMain, { duration: 260 });
  const secRoll = createRoller(tSec, { duration: 260 });

  let expanded = false;
  let wasPending = false;
  let curKey;          // segment id of the running activity (undefined until the first day arrives)
  let startMs = 0;
  let timerId = 0;
  let lastActs = null;

  // ---------- live timer: one text node per second, nothing else moves ----------
  function tick() {
    const sec = Math.max(0, Math.floor((app.nowMs() - startMs) / 1000));
    const hh = Math.floor(sec / 3600);
    const mm = Math.floor((sec % 3600) / 60);
    mainRoll.set(hh ? `${hh}:${T.pad2(mm)}` : String(mm));
    secRoll.set(`:${T.pad2(sec % 60)}`);
  }
  function loop() {
    clearTimeout(timerId);
    if (!startMs || document.hidden) return;
    tick();
    timerId = setTimeout(loop, 1000 - ((app.nowMs() - startMs) % 1000) + 8);
  }
  document.addEventListener('visibilitychange', loop);

  // ---------- inline ping answer ----------
  function setExpanded(on) {
    if (expanded === on) return;
    expanded = on;
    setData(root, 'ping', on ? 'true' : 'false');
    ping.inert = !on;
    add.setAttribute('aria-label', on ? 'Ответить позже' : 'Записать прошлое');
    syncLift();
  }
  function syncLift() {
    const grow = app.state.day?.current ? GROW_RUNNING : GROW_IDLE;
    root.style.setProperty('--e', `${grow}px`);
    document.documentElement.style.setProperty('--c-lift', expanded ? `${grow}px` : '0px');
  }

  function renderChips(s) {
    lastActs = s.activities;
    chips.replaceChildren(
      ...s.activities.slice(0, 6).map((a) => h('button', {
        type: 'button', class: 'c-ping-chip', dataset: { group: categoryInfo(a.category, s.me).group, cat: a.category ?? '' },
        onClick: () => { if (app.state.busy) return; setExpanded(false); app.actions.start({ activityId: a.id }); },
      }, a.name)),
      h('button', { type: 'button', class: 'c-ping-chip', dataset: { more: 'true' }, onClick: () => { setExpanded(false); flows.openSwitcher(); } }, 'Другое'));
  }

  function render(s) {
    if (s.phase !== 'ready' || !s.day) return;
    const c = s.day.current ?? null;
    const key = c?.segmentId ?? null;
    if (key !== curKey) {
      // Answered some other way (the switcher, a notification button, another device): fold back.
      if (curKey !== undefined && expanded) setExpanded(false);
      curKey = key;
      startMs = c ? Date.parse(c.startUtc) : 0;
      loop();
    } else if (c && Date.parse(c.startUtc) !== startMs) {
      startMs = Date.parse(c.startUtc);
      loop();
    }

    // A ping: answer in place. With a sheet already open the island is out of reach — the switcher takes it.
    if (s.ping.requested) {
      app.actions.clearPing();
      if (flows.top) flows.openSwitcher({ ping: true }); else setExpanded(true);
    }
    if (s.ping.pending && !wasPending && !flows.top) setExpanded(true);
    if (!s.ping.pending && wasPending) setExpanded(false);
    wasPending = s.ping.pending;

    setData(root, 'ready', 'true');
    setData(root, 'run', c ? 'true' : 'false');
    setData(root, 'pending', s.ping.pending && !expanded ? 'true' : null);
    const info = c ? categoryInfo(c.category, s.me) : null;
    setData(root, 'group', info?.group ?? null);
    setData(root, 'cat', info?.key ?? null);
    setData(root, 'over', c && c.typicalMin && c.elapsedMin > c.typicalMin ? 'true' : null);
    setText(title, c ? c.title : 'Чем занят?');
    setText(question, c ? `Всё ещё «${c.title}»?` : 'Чем занят?');
    main.setAttribute('aria-label', c ? `${c.title}, идёт ${T.fmtDuration(c.elapsedMin)}. Сменить занятие` : 'Чем занят? Начать занятие');
    yes.disabled = no.disabled = !!s.busy;
    if (!c && s.activities !== lastActs) renderChips(s);
    syncLift();
  }

  // ---------- taps ----------
  main.addEventListener('click', () => {
    if (app.state.ping.pending && !expanded) { setExpanded(true); return; } // an unanswered ping: answer here first
    setExpanded(false);
    flows.openSwitcher();
  });
  add.addEventListener('click', () => { if (expanded) setExpanded(false); else flows.openAdd(); });
  yes.addEventListener('click', () => { setExpanded(false); app.actions.continueCurrent(); });
  no.addEventListener('click', () => { setExpanded(false); app.actions.stop(); });
  other.addEventListener('click', () => { setExpanded(false); flows.openSwitcher(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && expanded && !flows.top) setExpanded(false); });

  // Swipe up on the island opens the switcher.
  let g = null;
  root.addEventListener('pointerdown', (e) => { g = e.target.closest('.c-ping-chips') ? null : { id: e.pointerId, x: e.clientX, y: e.clientY }; }, { passive: true });
  root.addEventListener('pointermove', (e) => {
    if (!g || e.pointerId !== g.id) return;
    if (g.y - e.clientY > 28 && Math.abs(e.clientX - g.x) < 40) {
      g = null;
      swallow = performance.now() + 400;
      setExpanded(false);
      flows.openSwitcher();
    }
  }, { passive: true });
  const end = () => { g = null; };
  root.addEventListener('pointerup', end, { passive: true });
  root.addEventListener('pointercancel', end, { passive: true });
  let swallow = 0;
  root.addEventListener('click', (e) => { if (performance.now() < swallow) { e.stopPropagation(); e.preventDefault(); } }, true);

  add.setAttribute('aria-label', 'Записать прошлое');
  return { render, expand: () => setExpanded(true), get expanded() { return expanded; } };
}
