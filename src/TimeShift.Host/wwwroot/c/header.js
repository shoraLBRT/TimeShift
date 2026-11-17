// Design C «Нить» — the header: a huge thin day number, the month, the ring of confirmed time, three dim dots.
import { setText, setData } from '/core/dom.js';
import * as T from '/core/time.js';
import { createRoller, play } from './util.js';

export function createHeader(app, flows, { main, navigate }) {
  const $ = (id) => document.getElementById(id);
  const weekday = $('weekday');
  const month = $('month');
  const daterow = $('daterow');
  const datepick = $('datepick');
  const todayBtn = $('today');
  const ringBtn = $('summary');
  const ringValue = $('ring-value');
  const ringNum = $('ring-num');
  const roll = createRoller($('daynum'), { duration: 420, distance: '0.38em' });
  let shown = null;
  let shownToday = null;
  let lastStatus;

  function renderDate(s) {
    const date = s.viewDate;
    const dir = shown ? Math.sign(T.diffDays(shown, date)) || 1 : 1;
    const [y, m, d] = date.split('-').map(Number);
    roll.set(String(d), dir);
    const monthText = T.MONTHS_GEN[m - 1] + (y !== Number(s.today.slice(0, 4)) ? ` ${y}` : '');
    if (month.textContent !== monthText) {
      setText(month, monthText);
      if (shown) play(month, [{ opacity: 0 }, { opacity: 1 }], { duration: 320, easing: 'ease-out' });
    }
    const diff = T.diffDays(s.today, date);
    const rel = diff === -1 ? 'вчера · ' : diff === 1 ? 'завтра · ' : '';
    const wd = rel + T.WEEKDAYS[T.weekday(date) - 1];
    if (weekday.textContent !== wd) {
      setText(weekday, wd);
      if (shown) play(weekday, [{ opacity: 0 }, { opacity: 1 }], { duration: 320, easing: 'ease-out' });
    }
    daterow.setAttribute('aria-label', T.capitalize(T.fmtDateLabel(date, s.today)));
    datepick.value = date;
    const isToday = date === s.today;
    setData(main, 'today', isToday ? 'true' : 'false');
    todayBtn.tabIndex = isToday ? -1 : 0;
    todayBtn.setAttribute('aria-hidden', isToday ? 'true' : 'false');
    setData(todayBtn, 'dir', date > s.today ? 'back' : 'fwd');
    shown = date;
    shownToday = s.today;
  }

  function renderRing(s) {
    const st = s.day && s.day.date === s.viewDate ? s.day.status : null;
    if (st === lastStatus) return;
    lastStatus = st;
    const total = st ? st.confirmedMin + st.assumedMin + st.questionableMin : 0;
    const pct = total ? Math.round((st.confirmedMin / total) * 100) : 0;
    setData(ringBtn, 'empty', total ? null : 'true');
    setData(ringBtn, 'full', pct === 100 ? 'true' : null);
    ringValue.style.strokeDashoffset = String(100 - pct);
    ringValue.style.opacity = pct > 0 ? '1' : '0';
    setText(ringNum, total ? String(pct) : '');
    ringBtn.setAttribute('aria-label', total ? `Итоги: подтверждено ${pct}% прошедшего времени` : 'Итоги');
  }

  function render(s) {
    if (s.phase !== 'ready' || !s.viewDate) return;
    if (s.viewDate !== shown || s.today !== shownToday) renderDate(s);
    renderRing(s);
    $('offline').hidden = s.online !== false;
  }

  ringBtn.addEventListener('click', () => flows.openSummary());
  $('settings').addEventListener('click', () => flows.openSettings());
  todayBtn.addEventListener('click', () => navigate(app.state.today));
  datepick.addEventListener('change', () => { if (T.isDate(datepick.value)) navigate(datepick.value); else datepick.value = app.state.viewDate; });
  datepick.addEventListener('click', () => { try { datepick.showPicker?.(); } catch { /* needs a gesture; the native tap already opened it */ } });

  return { render };
}
