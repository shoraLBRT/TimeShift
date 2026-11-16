// "What are you doing?" — one tap to continue / stop / start a recent activity. Used for pings and the now control.
import { h, setText } from '../dom.js';
import * as T from '../time.js';
import { button, chip, textInput, activitySuggest, recentChips } from './common.js';

/**
 * @param {object} app
 * @param {{ ping?: boolean, close?: Function, flows?: object }} opts
 */
export function switcherPanel(app, opts = {}) {
  const close = () => opts.close?.();
  const s0 = app.state;
  const cur = () => app.state.day?.current ?? null;

  const question = h('p', { class: 'ts-switcher-question' });
  const curTitle = h('span', { class: 'ts-current-title' });
  const curElapsed = h('span', { class: 'ts-current-elapsed' });
  const curSince = h('span', { class: 'ts-current-since' });
  const cont = button('Продолжаю', { variant: 'primary', action: 'continue', attrs: { 'data-busy-disable': '' }, onClick: () => { close(); app.actions.continueCurrent(); } });
  const stop = button('Закончил', { action: 'stop', attrs: { 'data-busy-disable': '' }, onClick: () => { close(); app.actions.stop(); } });

  // "Finished earlier": minutes ago chips or an exact time.
  const earlierToggle = h('button', { type: 'button', class: 'ts-link', dataset: { action: 'stop-earlier' }, 'aria-expanded': 'false' }, 'Закончил раньше…');
  const at = h('input', { type: 'time', class: 'ts-input ts-time', step: String((s0.me?.settings?.roundingMin ?? 5) * 60), 'aria-label': 'Во сколько закончил' });
  const atBtn = button('Закончил в это время', { variant: 'secondary', action: 'stop-at', attrs: { 'data-busy-disable': '' } });
  const earlierErr = h('p', { class: 'ts-field-error', role: 'alert', hidden: true });
  const stopAt = (ms) => {
    const c = cur();
    if (!c) return;
    const startMs = Date.parse(c.startUtc);
    if (ms <= startMs) { earlierErr.hidden = false; setText(earlierErr, `Раньше начала (${T.fmtClock(T.nowMinIn(tz(), startMs))}).`); return; }
    close();
    app.actions.stop({ at: T.utcToLocal(ms, tz()) });
  };
  const tz = () => app.state.me?.timeZone ?? T.deviceTimeZone();
  const agoChips = h('div', { class: 'ts-chips', dataset: { kind: 'ago' }, role: 'group', 'aria-label': 'Сколько минут назад' },
    [5, 15, 30, 60].map((m) => chip(`${T.fmtDuration(m)} назад`, { data: { minutes: m }, onClick: () => stopAt(app.nowMs() - m * 60000) })));
  atBtn.addEventListener('click', () => {
    if (!at.value) { at.focus(); return; }
    const today = T.todayIn(tz(), app.nowMs());
    let ms = T.localToUtc(`${today}T${at.value}`, tz());
    if (ms > app.nowMs()) ms = T.localToUtc(`${T.addDays(today, -1)}T${at.value}`, tz());
    stopAt(ms);
  });
  const earlier = h('div', { class: 'ts-stop-earlier', hidden: true }, agoChips, h('div', { class: 'ts-stop-at' }, at, atBtn), earlierErr);
  earlierToggle.addEventListener('click', () => {
    earlier.hidden = !earlier.hidden;
    earlierToggle.setAttribute('aria-expanded', String(!earlier.hidden));
    if (!earlier.hidden && !at.value) at.value = T.fmtClock(T.floorTo(T.nowMinIn(tz(), app.nowMs()), app.state.me?.settings?.roundingMin ?? 5));
  });

  const current = h('div', { class: 'ts-current' },
    h('span', { class: 'ts-current-label' }, 'Сейчас'), curTitle, curElapsed, curSince,
    h('div', { class: 'ts-current-actions' }, cont, stop), earlierToggle, earlier);

  // Start something: recent chips + text.
  const recentHolder = h('div', { class: 'ts-recent-holder' });
  const input = textInput({ placeholder: 'Чем занят?', enterkeyhint: 'go' });
  input.setAttribute('aria-label', 'Занятие или фраза');
  const startKnown = (a) => { close(); app.actions.start({ activityId: a.id }); };
  const suggest = activitySuggest(input, () => app.state.activities, startKnown);
  const go = button('Начать', { variant: 'primary', type: 'submit', action: 'start', attrs: { 'data-busy-disable': '' } });
  const quick = h('form', { class: 'ts-quick', novalidate: true }, h('div', { class: 'ts-combo' }, input, suggest.el), go);
  quick.addEventListener('submit', (e) => {
    e.preventDefault();
    const text = input.value.trim();
    if (!text) { input.focus(); return; }
    const known = app.state.activities.find((a) => a.name.toLowerCase() === text.toLowerCase());
    if (known) { startKnown(known); return; }
    close();
    if (opts.flows) opts.flows.openAssistant(text);
    else app.actions.sendMessage(text);
  });

  const more = h('div', { class: 'ts-switcher-more' },
    opts.flows ? button('Записать прошлое', { action: 'add-past', onClick: () => { close(); opts.flows.openAdd(); } }) : null,
    opts.flows ? button('Написать фразой', { action: 'assistant', onClick: () => { close(); opts.flows.openAssistant(); } }) : null);

  const root = h('section', { class: 'ts-switcher', dataset: { ping: opts.ping ? 'true' : 'false' } },
    question, current, h('h3', { class: 'ts-switcher-subtitle' }, 'Начать другое'), recentHolder, quick, more);

  let lastActs = null;
  let lastCurId;
  const render = (s) => {
    const c = s.day?.current ?? null;
    root.dataset.running = c ? 'true' : 'false';
    current.hidden = !c;
    if (c) {
      setText(curTitle, c.title);
      setText(curElapsed, T.fmtDuration(c.elapsedMin));
      setText(curSince, `с ${T.fmtClock(T.nowMinIn(s.me?.timeZone ?? T.deviceTimeZone(), Date.parse(c.startUtc)))}`);
      const over = c.typicalMin && c.elapsedMin > c.typicalMin;
      current.dataset.overTypical = over ? 'true' : 'false';
    }
    setText(question, c ? (opts.ping ? `Всё ещё «${c.title}»?` : 'Что делаешь?') : 'Что делаешь сейчас?');
    if (s.activities !== lastActs || (c?.activityId ?? null) !== lastCurId) {
      lastActs = s.activities;
      lastCurId = c?.activityId ?? null;
      recentHolder.replaceChildren(s.activities.length
        ? recentChips(s.activities, startKnown, { exclude: lastCurId, me: s.me })
        : h('p', { class: 'ts-empty' }, 'Здесь появятся недавние занятия.'));
    }
    for (const b of root.querySelectorAll('[data-busy-disable]')) b.disabled = !!s.busy;
    if (s.busy) root.dataset.busy = s.busy; else delete root.dataset.busy;
  };
  const unsub = app.subscribe(render);
  if (opts.ping) app.actions.clearPing();
  return {
    el: root,
    title: opts.ping ? 'Напоминание' : 'Сейчас',
    destroy() { unsub(); suggest.destroy(); },
  };
}
