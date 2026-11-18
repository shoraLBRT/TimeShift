// Entry editor: edit a piece (stored segment or computed piece) or record a new past entry.
import { h, setText } from '../dom.js';
import * as T from '../time.js';
import { describePiece, pieceBounds, activityCategory } from '../timeline.js';
import { button, field, textInput, categoryPicker, activitySuggest, rangeControl, switchInput } from './common.js';

/**
 * @param {object} app
 * @param {{ piece?: object, parallel?: boolean, draft?: { startMin, endMin, title?, category?, parallel? },
 *           close?: Function, flows?: object }} opts
 *   piece  — a piece of state.day (main track or, with parallel: true, the parallel track)
 *   draft  — prefill for a new entry (minutes relative to the viewed day)
 */
export function entryPanel(app, opts = {}) {
  const s = app.state;
  const day = s.day;
  const me = s.me;
  const piece = opts.piece ?? null;
  const segment = piece?.segment ?? null;
  const mode = !piece ? 'new' : segment ? 'edit' : 'computed';
  const d = piece ? describePiece(piece, me, { parallel: opts.parallel }) : null;
  const step = me?.settings?.roundingMin ?? 5;
  const tz = me?.timeZone ?? T.deviceTimeZone();
  const date = day?.date ?? s.viewDate ?? s.today ?? T.todayIn(tz, app.nowMs()); // never null (rangeControl needs it)
  const nowDayMin = day?.nowMin ?? (date < s.today ? day?.dayMinutes ?? 1440 : 0);

  // Initial values.
  let startMin;
  let endMin;
  let openEnd = false;
  if (mode === 'edit') {
    const b = pieceBounds(piece, day);
    startMin = b.startMin;
    endMin = b.endMin;
    openEnd = b.end == null;
  } else if (mode === 'computed') {
    startMin = piece.startMin;
    endMin = Math.min(piece.endMin, nowDayMin || piece.endMin);
  } else if (opts.draft) {
    startMin = opts.draft.startMin;
    endMin = opts.draft.endMin;
  } else {
    endMin = date === s.today ? T.floorTo(nowDayMin, step) : 13 * 60;
    startMin = Math.max(0, endMin - 60);
  }
  const initial = {
    title: mode === 'new' ? opts.draft?.title ?? '' : d.unknown ? '' : piece.title,
    category: mode === 'new' ? opts.draft?.category ?? null : d.unknown ? null : piece.category ?? null,
  };

  const title = textInput({ value: initial.title, placeholder: d?.unknown || mode === 'new' ? 'Что это было?' : '', enterkeyhint: 'done' });
  const titleField = field('Занятие', title);
  let categoryTouched = false;
  const cat = categoryPicker(me, initial.category, () => { categoryTouched = true; refresh(); }, { noneLabel: mode === 'edit' ? null : 'По занятию' });
  // A picked activity brings its category: the rule for the part of the day the entry starts in, else its own.
  let picked = null;
  const expected = (a) => activityCategory(a, range.value().startMin);
  const followPicked = () => {
    if (categoryTouched || !picked || title.value.trim().toLowerCase() !== picked.name.toLowerCase()) return;
    const c = expected(picked);
    if (c && c !== cat.get()) cat.set(c);
  };
  const suggest = activitySuggest(title, () => app.state.activities, (a) => {
    picked = a;
    followPicked();
    refresh();
  });
  const range = rangeControl({ date, startMin, endMin, step, openEnd, onChange: () => { followPicked(); refresh(); } });
  const rangeError = h('p', { class: 'ts-field-error', role: 'alert', hidden: true });
  const parallelSw = mode === 'new' ? switchInput('Параллельно с основным', !!opts.draft?.parallel, () => refresh(), { hint: 'Например, ел во время работы: основное занятие не сдвинется.' }) : null;

  const confirmBtn = mode !== 'new' && piece.status === 'assumed' && !piece.future && !piece.open
    ? button('Всё верно', { variant: 'primary', action: 'confirm', attrs: { 'data-busy-disable': '' }, onClick: () => confirm() }) : null;
  const saveBtn = button(mode === 'new' ? 'Записать' : 'Сохранить', { variant: 'primary', type: 'submit', action: 'save', attrs: { 'data-busy-disable': '' } });
  const deleteBtn = mode === 'edit' ? button('Удалить', { variant: 'danger', action: 'delete', attrs: { 'data-busy-disable': '' }, onClick: () => remove() }) : null;

  const head = d ? h('div', { class: 'ts-entry-head' },
    h('span', { class: 'ts-entry-status', dataset: { kind: d.kind } }, d.statusLabel),
    d.sourceLabel ? h('span', { class: 'ts-entry-source', dataset: { source: d.source } }, d.sourceLabel) : null) : null;

  const form = h('form', { class: 'ts-entry', novalidate: true, dataset: { mode, status: piece?.status ?? 'new', kind: d?.kind ?? 'new', parallel: opts.parallel ? 'true' : 'false' } },
    head,
    h('div', { class: 'ts-combo' }, titleField.el, suggest.el),
    range.el, rangeError,
    cat.el,
    parallelSw?.el,
    h('div', { class: 'ts-entry-actions' }, confirmBtn, saveBtn, deleteBtn));

  const value = () => ({ title: title.value.trim(), category: cat.get(), range: range.value(), parallel: parallelSw?.input.checked ?? false });
  const dirty = () => {
    const v = value();
    if (mode === 'new') return true;
    return v.title !== initial.title || v.category !== initial.category || v.range.startMin !== startMin || (!openEnd && v.range.endMin !== endMin);
  };
  function refresh() {
    const isDirty = dirty();
    form.dataset.dirty = String(isDirty);
    if (confirmBtn) {
      confirmBtn.hidden = isDirty;
      saveBtn.dataset.variant = isDirty ? 'primary' : 'secondary';
      saveBtn.hidden = !isDirty;
    }
  }

  function validate() {
    const v = value();
    let ok = true;
    titleField.setError(null);
    rangeError.hidden = true;
    if (!v.title && (mode !== 'edit' || initial.title)) { titleField.setError('Напиши, что это было.'); ok = false; }
    if (v.title.length > 60) { titleField.setError('Не длиннее 60 символов.'); ok = false; }
    const r = v.range;
    let msg = null;
    if (!r.valid) msg = 'Укажи время.';
    else if (!openEnd && r.endMin - r.startMin > 1440) msg = 'Не больше суток.';
    else if (T.localToUtc(openEnd ? r.start : r.end, tz) > app.nowMs() + 60000) msg = 'Будущее не записывается.';
    if (msg) { rangeError.hidden = false; setText(rangeError, msg); ok = false; }
    return ok;
  }

  const showServerError = (e) => {
    if (!e) return;
    if (e.field === 'title') titleField.setError(e.message);
    else { rangeError.hidden = false; setText(rangeError, e.message); }
  };

  async function save() {
    if (!validate()) return;
    const v = value();
    const known = app.state.activities.find((a) => a.name.toLowerCase() === v.title.toLowerCase());
    let r;
    if (mode === 'edit') {
      if (!dirty()) { opts.close?.(); return; }
      const patch = {};
      if (v.range.startMin !== startMin) patch.start = v.range.start;
      if (!openEnd && v.range.endMin !== endMin) patch.end = v.range.end;
      if (v.title !== initial.title && v.title) patch.title = v.title;
      if (v.category !== initial.category) patch.category = v.category;
      r = await app.actions.updateEntry(segment.id, patch, { inlineErrors: true, title: v.title || initial.title, category: v.category, parallel: opts.parallel, range: openEnd ? null : { start: v.range.start, end: v.range.end } });
    } else {
      const entry = { start: v.range.start, end: v.range.end, ...(known ? { activityId: known.id } : { title: v.title }) };
      if (v.category && (categoryTouched || mode === 'computed' || !known || v.category !== activityCategory(known, v.range.startMin))) entry.category = v.category;
      if (v.parallel) entry.parallel = true;
      r = await app.actions.addEntry(entry, { inlineErrors: true });
    }
    if (r.ok) opts.close?.();
    else if (r.error?.status === 422) showServerError(r.error);
  }
  function confirm() {
    const b = { start: T.minToLocal(date, piece.startMin), end: T.minToLocal(date, Math.min(piece.endMin, nowDayMin || piece.endMin)) };
    opts.close?.();
    app.actions.confirm(b);
  }
  function remove() {
    opts.close?.();
    app.actions.deleteEntry(segment.id);
  }

  form.addEventListener('submit', (e) => { e.preventDefault(); save(); });
  title.addEventListener('input', refresh);
  refresh();

  const unsub = app.subscribe((st) => {
    for (const b of form.querySelectorAll('[data-busy-disable]')) b.disabled = !!st.busy;
    if (st.busy) form.dataset.busy = st.busy; else delete form.dataset.busy;
  });

  const titleText = mode === 'new' ? 'Новая запись' : d.unknown ? 'Не записано' : piece.title;
  return { el: form, title: titleText, destroy() { unsub(); suggest.destroy(); } };
}
