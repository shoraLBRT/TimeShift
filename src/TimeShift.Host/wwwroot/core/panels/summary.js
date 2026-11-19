// Summary: the viewed day by group/category with status shares, and the 7 days ending with it.
// Data comes from timeline.daySummary / timeline.rangeSummary; the markup is a default that designs may replace.
import { h } from '../dom.js';
import * as T from '../time.js';
import { daySummary, rangeSummary } from '../timeline.js';

const pct = (x) => `${Math.round(x * 100)}%`;

export function totalsList(items, kind) {
  if (!items.length) return h('p', { class: 'ts-empty' }, 'Пока ничего не учтено.');
  return h('ul', { class: 'ts-totals', dataset: { kind } }, items.map((t) => h('li', {
    class: 'ts-total', dataset: { group: t.group, category: t.key ?? '', unknown: t.unknown ? 'true' : null }, style: { '--share': t.share.toFixed(4) },
  }, h('span', { class: 'ts-total-name' }, t.label ?? t.name), h('span', { class: 'ts-total-value' }, t.durationText), h('span', { class: 'ts-total-share' }, pct(t.share)),
    h('span', { class: 'ts-bar', 'aria-hidden': 'true' }))));
}

export function statusBar(status) {
  const label = status.parts.map((p) => `${p.label.toLowerCase()} ${pct(p.share)}`).join(', ');
  return h('div', { class: 'ts-status' },
    h('div', { class: 'ts-status-bar', role: 'img', 'aria-label': label }, status.parts.map((p) => h('span', { class: 'ts-status-part', dataset: { status: p.status }, style: { '--share': p.share.toFixed(4) } }))),
    h('ul', { class: 'ts-status-legend' }, status.parts.map((p) => h('li', { class: 'ts-status-item', dataset: { status: p.status } },
      h('span', { class: 'ts-status-label' }, p.label), h('span', { class: 'ts-status-value' }, p.durationText)))));
}

/**
 * @param {object} app
 * @param {{ close?: Function, days?: number }} opts
 * @returns {{ el, title, destroy, data: { day, range } }}  data is filled when loaded
 */
export function summaryPanel(app, opts = {}) {
  const s = app.state;
  const data = { day: daySummary(s.day, s.me), range: null };
  const end = s.viewDate ?? s.today;
  const from = T.addDays(end, -((opts.days ?? 7) - 1));

  const dayEl = h('section', { class: 'ts-summary-day', 'aria-labelledby': 'ts-sum-day' },
    h('h3', { class: 'ts-summary-title', id: 'ts-sum-day' }, T.fmtDateLabel(end, s.today)));
  if (data.day) {
    dayEl.append(
      h('p', { class: 'ts-summary-total' }, `Учтено ${T.fmtDuration(data.day.totals.minutes)}`),
      statusBar(data.day.status),
      h('h4', { class: 'ts-summary-subtitle' }, 'По группам'), totalsList(data.day.totals.groups.map((g) => ({ ...g, key: null })), 'group'),
      h('h4', { class: 'ts-summary-subtitle' }, 'По категориям'), totalsList(data.day.totals.categories, 'category'),
      data.day.parallelMin ? h('p', { class: 'ts-summary-parallel' }, `Параллельно: ${T.fmtDuration(data.day.parallelMin)}`) : null);
  } else dayEl.append(h('p', { class: 'ts-empty' }, 'День ещё загружается.'));

  const weekEl = h('section', { class: 'ts-summary-week', dataset: { loading: 'true' }, 'aria-labelledby': 'ts-sum-week', 'aria-busy': 'true' },
    h('h3', { class: 'ts-summary-title', id: 'ts-sum-week' }, `${opts.days ?? 7} дней`),
    h('p', { class: 'ts-summary-span' }, T.fmtDateSpan(from, end)),
    h('p', { class: 'ts-loading' }, 'Загружаю…'));

  const root = h('section', { class: 'ts-summary' }, dayEl, weekEl);
  let alive = true;
  app.actions.loadSummary({ from, to: end }).then((raw) => {
    if (!alive) return;
    weekEl.dataset.loading = 'false';
    weekEl.removeAttribute('aria-busy');
    weekEl.querySelector('.ts-loading')?.remove();
    if (!raw) { weekEl.append(h('p', { class: 'ts-empty' }, 'Не удалось загрузить.')); return; }
    const r = rangeSummary(raw, app.state.me);
    data.range = r;
    weekEl.append(
      h('ol', { class: 'ts-week' }, r.days.map((dd) => h('li', { class: 'ts-week-day', dataset: { date: dd.date, weekday: T.weekday(dd.date) }, 'aria-current': dd.date === end ? 'date' : null },
        h('button', { type: 'button', class: 'ts-week-open', onClick: () => { app.actions.goToDay(dd.date); opts.close?.(); }, 'aria-label': `${T.fmtDateLabel(dd.date, app.state.today)}: подтверждено ${dd.confirmedText}` },
          h('span', { class: 'ts-week-label' }, T.WEEKDAYS_SHORT[T.weekday(dd.date) - 1]),
          h('span', { class: 'ts-week-bar', style: { '--fill': (dd.minutes / r.maxMinutes).toFixed(4) }, 'aria-hidden': 'true' },
            dd.groups.map((g) => h('span', { class: 'ts-week-seg', dataset: { group: g.group }, style: { '--share': (g.minutes / Math.max(1, dd.minutes)).toFixed(4) } }))),
          h('span', { class: 'ts-week-total', title: 'Подтверждено', dataset: { confirmedShare: dd.confirmedShare.toFixed(4) } }, dd.confirmedText))))),
      statusBar(r.status),
      h('h4', { class: 'ts-summary-subtitle' }, 'По группам'), totalsList(r.totals.groups.map((g) => ({ ...g, key: null })), 'group'),
      h('h4', { class: 'ts-summary-subtitle' }, 'По категориям'), totalsList(r.totals.categories, 'category'));
  });
  return { el: root, title: 'Итоги', data, destroy() { alive = false; } };
}
