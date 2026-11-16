// "What was it?" for a questionable piece: one tap on a recent activity records the whole range.
import { h } from '../dom.js';
import * as T from '../time.js';
import { describePiece } from '../timeline.js';
import { button, textInput, activitySuggest, recentChips } from './common.js';

/**
 * @param {object} app
 * @param {{ piece: object, close?: Function, flows?: object }} opts
 */
export function gapPanel(app, opts) {
  const s = app.state;
  const day = s.day;
  const piece = opts.piece;
  const d = describePiece(piece, s.me);
  const endMin = day?.nowMin != null ? Math.min(piece.endMin, day.nowMin) : piece.endMin;
  const start = T.minToLocal(day.date, piece.startMin);
  const end = T.minToLocal(day.date, endMin);
  const close = () => opts.close?.();

  const record = (body) => { close(); app.actions.addEntry({ start, end, ...body }); };
  const input = textInput({ placeholder: 'Что это было?', enterkeyhint: 'done' });
  input.setAttribute('aria-label', 'Что это было');
  const suggest = activitySuggest(input, () => app.state.activities, (a) => record({ activityId: a.id }));
  const submit = button('Записать', { variant: 'primary', type: 'submit', action: 'record', attrs: { 'data-busy-disable': '' } });
  const form = h('form', { class: 'ts-quick', novalidate: true }, h('div', { class: 'ts-combo' }, input, suggest.el), submit);
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    const t = input.value.trim();
    if (!t) { input.focus(); return; }
    const known = app.state.activities.find((a) => a.name.toLowerCase() === t.toLowerCase());
    record(known ? { activityId: known.id } : { title: t });
  });

  const nothing = button('Ничего особенного', { action: 'nothing', attrs: { 'data-busy-disable': '' }, onClick: () => { close(); app.actions.confirm({ start, end, includeQuestionable: true }); } });
  const part = opts.flows ? button('Только часть времени', { action: 'split', onClick: () => { close(); opts.flows.openAdd({ startMin: piece.startMin, endMin: Math.min(endMin, piece.startMin + 30) }); } }) : null;

  const root = h('section', { class: 'ts-gap', dataset: { source: piece.source } },
    h('p', { class: 'ts-gap-range' }, h('span', { class: 'ts-gap-time' }, T.fmtRange(piece.startMin, endMin)), h('span', { class: 'ts-gap-duration' }, T.fmtDuration(endMin - piece.startMin))),
    h('p', { class: 'ts-gap-question' }, piece.source === 'background' ? 'Что это было?' : `Это всё ещё «${d.title}»?`),
    recentChips(s.activities, (a) => record({ activityId: a.id }), { me: s.me }),
    form,
    h('div', { class: 'ts-gap-actions' }, nothing, part),
    h('p', { class: 'ts-hint' }, '«Ничего особенного» запишет это время как залипание.'));

  const unsub = app.subscribe((st) => {
    for (const b of root.querySelectorAll('[data-busy-disable]')) b.disabled = !!st.busy;
  });
  return { el: root, title: d.title, destroy() { unsub(); suggest.destroy(); } };
}
