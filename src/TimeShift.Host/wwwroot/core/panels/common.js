// Shared building blocks for panels: semantic markup with stable ts-* classes, no visual design.
import { h, uid, setText } from '../dom.js';
import { categoriesByGroup, categoryInfo } from '../timeline.js';
import * as T from '../time.js';

/** <button class="ts-button" data-variant data-action> */
export function button(label, { variant = 'secondary', action, onClick, type = 'button', disabled, className, attrs } = {}) {
  return h('button', { type, class: ['ts-button', className], dataset: { variant, action }, onClick, disabled, ...attrs }, label);
}

/** <button class="ts-chip" aria-pressed?> */
export function chip(label, { pressed, onClick, data = {}, className, disabled, title } = {}) {
  return h('button', { type: 'button', class: ['ts-chip', className], 'aria-pressed': pressed == null ? null : String(!!pressed), dataset: data, onClick, disabled, title }, label);
}

/** <label class="ts-field"><span class="ts-field-label"> control <span class="ts-field-hint"> <span class="ts-field-error"> */
export function field(label, control, { hint, className } = {}) {
  const id = control.id || (control.id = uid('ts-f'));
  const error = h('span', { class: 'ts-field-error', id: `${id}-err`, role: 'alert', hidden: true });
  const hintEl = hint ? h('span', { class: 'ts-field-hint', id: `${id}-hint` }, hint) : null;
  if (hintEl) control.setAttribute('aria-describedby', hintEl.id);
  const el = h('label', { class: ['ts-field', className], htmlFor: id }, h('span', { class: 'ts-field-label' }, label), control, hintEl, error);
  return {
    el, control,
    setError(msg) {
      error.hidden = !msg;
      setText(error, msg ?? '');
      el.dataset.invalid = msg ? 'true' : 'false';
      if (msg) control.setAttribute('aria-invalid', 'true'); else control.removeAttribute('aria-invalid');
      control.setAttribute('aria-errormessage', error.id);
    },
  };
}

export function textInput({ value = '', placeholder, maxLength = 60, autocomplete = 'off', enterkeyhint, className, name, type = 'text', inputmode } = {}) {
  return h('input', { type, class: ['ts-input', className], value, placeholder, maxlength: maxLength, autocomplete, enterkeyhint, name, inputmode, autocapitalize: 'sentences', spellcheck: 'false' });
}

export function timeInput(value, { step = 5, className, disabled } = {}) {
  return h('input', { type: 'time', class: ['ts-input', 'ts-time', className], value, step: String(Math.max(1, step) * 60), disabled, required: true });
}

/** <label class="ts-switch"><input type=checkbox role=switch><span class="ts-switch-label"> */
export function switchInput(label, checked, onChange, { hint, disabled } = {}) {
  const input = h('input', { type: 'checkbox', role: 'switch', class: 'ts-switch-input', checked, disabled, onChange: () => onChange(input.checked, input) });
  const el = h('label', { class: 'ts-switch' }, input, h('span', { class: 'ts-switch-label' }, label), hint ? h('span', { class: 'ts-switch-hint' }, hint) : null);
  return { el, input, set(v) { input.checked = !!v; } };
}

/** Radio group: <div class="ts-options" role="radiogroup"><label class="ts-option"><input type=radio><span class="ts-option-label"> */
export function radioGroup(label, options, value, onChange, { className } = {}) {
  const name = uid('ts-r');
  const inputs = [];
  const el = h('div', { class: ['ts-options', className], role: 'radiogroup', 'aria-label': label },
    options.map((o) => {
      const input = h('input', { type: 'radio', name, value: String(o.value), checked: String(o.value) === String(value), class: 'ts-option-input', onChange: () => { if (input.checked) onChange(o.value); } });
      inputs.push(input);
      return h('label', { class: 'ts-option', dataset: { value: String(o.value) } }, input,
        h('span', { class: 'ts-option-label' }, o.label), o.hint ? h('span', { class: 'ts-option-hint' }, o.hint) : null);
    }));
  return { el, set(v) { for (const i of inputs) i.checked = i.value === String(v); } };
}

/** Weekday chips (ISO 1..7): <div class="ts-weekdays" role="group"> */
export function weekdayChips(days, onChange) {
  let value = new Set(days);
  const chips = T.WEEKDAYS_SHORT.map((label, i) => {
    const d = i + 1;
    const c = chip(label, { pressed: value.has(d), data: { day: d }, title: T.WEEKDAYS[i] });
    c.setAttribute('aria-label', T.WEEKDAYS[i]);
    c.addEventListener('click', () => {
      if (value.has(d)) value.delete(d); else value.add(d);
      c.setAttribute('aria-pressed', String(value.has(d)));
      onChange([...value].sort());
    });
    return c;
  });
  const el = h('div', { class: 'ts-weekdays', role: 'group', 'aria-label': 'Дни недели' }, chips);
  return { el, get: () => [...value].sort(), set(v) { value = new Set(v); chips.forEach((c, i) => c.setAttribute('aria-pressed', String(value.has(i + 1)))); } };
}

/**
 * Category picker: <details class="ts-category-picker"><summary> + groups of chips.
 * @param {object} me
 * @param {string|null} value
 * @param {(key: string|null) => void} onChange
 * @param {{ noneLabel?: string }} [o] noneLabel adds a first chip for "no explicit category" (null)
 */
export function categoryPicker(me, value, onChange, o = {}) {
  let current = value ?? null;
  const summaryValue = h('span', { class: 'ts-category-value' });
  const summary = h('summary', { class: 'ts-category-summary' }, h('span', { class: 'ts-category-caption' }, o.caption ?? 'Категория'), summaryValue);
  const chips = [];
  const mk = (key, label, extra = {}) => {
    const c = chip(label, { pressed: key === current, data: { category: key ?? '', group: extra.group ?? 'none', depth: extra.depth ?? 0 } });
    c.addEventListener('click', () => { set(key); onChange(key); el.open = false; });
    chips.push([key, c]);
    return c;
  };
  const groups = categoriesByGroup(me).map((g) => h('div', { class: 'ts-category-group', dataset: { group: g.group }, role: 'group', 'aria-label': g.name },
    h('span', { class: 'ts-category-group-name' }, g.name),
    h('div', { class: 'ts-chips' }, g.categories.map((c) => mk(c.key, c.name, { group: g.group, depth: c.depth })))));
  const el = h('details', { class: 'ts-category-picker' }, summary,
    h('div', { class: 'ts-category-groups' }, o.noneLabel ? h('div', { class: 'ts-category-group', dataset: { group: 'none' } }, h('div', { class: 'ts-chips' }, mk(null, o.noneLabel))) : null, groups));
  function set(key) {
    current = key ?? null;
    for (const [k, c] of chips) c.setAttribute('aria-pressed', String(k === current));
    const info = categoryInfo(current, me);
    setText(summaryValue, current ? info.name : (o.noneLabel ?? info.name));
    el.dataset.group = current ? info.group : 'none';
  }
  set(current);
  return { el, get: () => current, set };
}

/**
 * Autocomplete from activities under a text input (combobox pattern).
 * @param {HTMLInputElement} input
 * @param {() => Array<{id, name, category}>} source
 * @param {(activity) => void} onPick
 * @returns {{ el, close(), destroy() }}
 */
export function activitySuggest(input, source, onPick, { limit = 6 } = {}) {
  const listId = uid('ts-sg');
  const el = h('ul', { class: 'ts-suggest', id: listId, role: 'listbox', hidden: true });
  input.setAttribute('role', 'combobox');
  input.setAttribute('aria-autocomplete', 'list');
  input.setAttribute('aria-expanded', 'false');
  input.setAttribute('aria-controls', listId);
  let items = [];
  let active = -1;
  const render = () => {
    const q = input.value.trim().toLowerCase();
    const all = source();
    items = !q ? [] : all
      .filter((a) => a.name.toLowerCase().includes(q) && a.name.toLowerCase() !== q)
      .sort((a, b) => Number(!a.name.toLowerCase().startsWith(q)) - Number(!b.name.toLowerCase().startsWith(q)))
      .slice(0, limit);
    active = -1;
    el.replaceChildren(...items.map((a, i) => h('li', { role: 'option', id: `${listId}-${i}`, class: 'ts-suggest-item', 'aria-selected': 'false', dataset: { activityId: a.id } },
      h('button', { type: 'button', class: 'ts-suggest-button', tabindex: '-1', onPointerdown: (e) => e.preventDefault(), onClick: () => pick(i) }, a.name))));
    el.hidden = !items.length;
    input.setAttribute('aria-expanded', String(!!items.length));
    input.removeAttribute('aria-activedescendant');
  };
  const highlight = (i) => {
    active = i;
    [...el.children].forEach((li, j) => li.setAttribute('aria-selected', String(j === i)));
    if (i >= 0) input.setAttribute('aria-activedescendant', `${listId}-${i}`); else input.removeAttribute('aria-activedescendant');
  };
  const pick = (i) => { const a = items[i]; if (!a) return; input.value = a.name; close(); onPick(a); };
  const close = () => { items = []; el.hidden = true; el.replaceChildren(); input.setAttribute('aria-expanded', 'false'); };
  const onKey = (e) => {
    if (el.hidden) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); highlight(Math.min(items.length - 1, active + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); highlight(Math.max(-1, active - 1)); }
    else if (e.key === 'Enter' && active >= 0) { e.preventDefault(); e.stopPropagation(); pick(active); }
    else if (e.key === 'Escape') { e.stopPropagation(); e.preventDefault(); close(); }
  };
  const onBlur = () => setTimeout(close, 120);
  input.addEventListener('input', render);
  input.addEventListener('keydown', onKey);
  input.addEventListener('blur', onBlur);
  return { el, close, destroy() { input.removeEventListener('input', render); input.removeEventListener('keydown', onKey); input.removeEventListener('blur', onBlur); } };
}

/** Recent activities as one-tap chips: <ul class="ts-recent"><li><button class="ts-chip ts-activity-chip"> */
export function recentChips(activities, onPick, { exclude, limit = 8, me } = {}) {
  const list = activities.filter((a) => a.id !== exclude).slice(0, limit);
  return h('ul', { class: 'ts-recent', 'aria-label': 'Недавние занятия' }, list.map((a) => h('li', { class: 'ts-recent-item' },
    chip(a.name, { className: 'ts-activity-chip', data: { activityId: a.id, category: a.category ?? '', group: categoryInfo(a.category, me).group }, onClick: () => onPick(a) }))));
}

/**
 * Time range control for a day: two <input type=time>, «+1 день» when the end crosses midnight, duration text,
 * quick duration chips. Values are minutes relative to `date` (start may be negative for a segment begun the day
 * before). Output: local time strings.
 */
export function rangeControl({ date: dateIn, startMin, endMin, step = 5, openEnd = false, durations = [15, 30, 60, 90, 120], onChange }) {
  const date = dateIn ?? T.todayIn(T.deviceTimeZone()); // guard: called before the app knows its day
  const startDayShift = Math.floor(startMin / 1440);
  const anchor = T.addDays(date, startDayShift);
  const start = timeInput(T.fmtClock(startMin), { step, className: 'ts-time-start' });
  const end = timeInput(openEnd ? '' : T.fmtClock(endMin), { step, className: 'ts-time-end', disabled: openEnd });
  start.setAttribute('aria-label', 'Начало');
  end.setAttribute('aria-label', 'Конец');
  const nextDay = h('span', { class: 'ts-range-next-day', hidden: true }, '+1 день');
  const prevDay = startDayShift < 0 ? h('span', { class: 'ts-range-prev-day' }, T.fmtDateChip(anchor)) : null;
  const duration = h('output', { class: 'ts-range-duration', 'aria-live': 'polite' });
  const chips = openEnd ? null : h('div', { class: 'ts-chips', dataset: { kind: 'duration' }, role: 'group', 'aria-label': 'Длительность' },
    durations.map((d) => chip(T.fmtDuration(d), { data: { minutes: d }, onClick: () => { setEnd(read().startMin + d); emit(); } })));
  const el = h('div', { class: 'ts-range', dataset: { open: openEnd ? 'true' : 'false' } },
    h('div', { class: 'ts-range-row' },
      h('label', { class: 'ts-range-field', dataset: { edge: 'start' } }, h('span', { class: 'ts-field-label' }, 'С'), start, prevDay),
      h('label', { class: 'ts-range-field', dataset: { edge: 'end' } }, h('span', { class: 'ts-field-label' }, 'До'), openEnd ? h('span', { class: 'ts-range-now' }, 'сейчас') : end, nextDay)),
    duration, chips);
  if (openEnd) end.hidden = true;

  function read() {
    const s = T.parseClock(start.value);
    const sm = startDayShift * 1440 + s;
    if (openEnd) return { startMin: sm, endMin: null };
    let e = T.parseClock(end.value);
    let em = startDayShift * 1440 + e;
    if (em <= sm) em += 1440;
    return { startMin: sm, endMin: em };
  }
  function setEnd(min) { end.value = T.fmtClock(min); }
  function emit() { update(); onChange?.(value()); }
  function update() {
    const r = read();
    const crosses = !openEnd && r.endMin != null && Math.floor(r.endMin / 1440) > Math.floor(r.startMin / 1440) && r.endMin % 1440 !== 0;
    nextDay.hidden = !crosses;
    el.dataset.crossesMidnight = crosses ? 'true' : 'false';
    if (openEnd || Number.isNaN(r.startMin) || Number.isNaN(r.endMin)) setText(duration, '');
    else setText(duration, T.fmtDuration(r.endMin - r.startMin));
    if (chips) for (const c of chips.children) c.setAttribute('aria-pressed', String(r.endMin - r.startMin === Number(c.dataset.minutes)));
  }
  function value() {
    const r = read();
    return {
      ...r,
      valid: !Number.isNaN(r.startMin) && (openEnd || !Number.isNaN(r.endMin)),
      start: Number.isNaN(r.startMin) ? null : T.minToLocal(date, r.startMin),
      end: r.endMin == null || Number.isNaN(r.endMin) ? null : T.minToLocal(date, r.endMin),
    };
  }
  start.addEventListener('change', emit);
  end.addEventListener('change', emit);
  start.addEventListener('input', update);
  end.addEventListener('input', update);
  update();
  return { el, value, start, end, setEnd(min) { setEnd(min); update(); }, setStart(min) { start.value = T.fmtClock(min); update(); } };
}

/** Reflect app.busy on a root: data-busy="<action>" and disable [data-busy-disable] controls. */
export function bindBusy(app, root) {
  return app.subscribe((s) => {
    if (s.busy) root.dataset.busy = s.busy; else delete root.dataset.busy;
    for (const b of root.querySelectorAll('[data-busy-disable]')) b.disabled = !!s.busy;
  });
}

/** Small note block: <p class="ts-note" data-kind> */
export const note = (text, kind = 'info') => h('p', { class: 'ts-note', dataset: { kind } }, text);
