// Regular blocks of the week template: list and form (used by onboarding and settings).
import { h, setText } from '../dom.js';
import * as T from '../time.js';
import { button, chip, field, textInput, timeInput, weekdayChips, categoryPicker } from './common.js';

export const BLOCK_PRESETS = [
  { key: 'work', label: 'Работа', block: { kind: 'fixed', title: 'Работа', category: 'work', days: [1, 2, 3, 4, 5], start: '10:00', end: '19:00' } },
  { key: 'study', label: 'Учёба', block: { kind: 'fixed', title: 'Учёба', category: 'study', days: [1, 2, 3, 4, 5], start: '09:00', end: '15:00' } },
  { key: 'custom', label: 'Своё', block: { kind: 'custom', title: '', category: null, days: [1, 2, 3, 4, 5], start: '19:00', end: '20:00' } },
];

/** [1,2,3,4,5] → 'пн–пт'; [6,7] → 'сб, вс'; all → 'каждый день'. */
export function fmtDays(days) {
  const d = [...days].sort();
  if (d.length === 7) return 'каждый день';
  const parts = [];
  for (let i = 0; i < d.length;) {
    let j = i;
    while (j + 1 < d.length && d[j + 1] === d[j] + 1) j++;
    const a = T.WEEKDAYS_SHORT[d[i] - 1];
    const b = T.WEEKDAYS_SHORT[d[j] - 1];
    parts.push(j - i >= 2 ? `${a}–${b}` : j > i ? `${a}, ${b}` : a);
    i = j + 1;
  }
  return parts.join(', ');
}

export const fmtBlockTime = (b) => `${b.start}–${b.end}${T.parseClock(b.end) <= T.parseClock(b.start) ? ' (+1 день)' : ''}`;

/**
 * List of blocks: <ul class="ts-block-list"><li class="ts-block-item" data-block-id>…
 * @param {{ onEdit?: (block) => void }} o  onEdit makes the item a button (settings); otherwise only «Удалить»
 */
export function blockList(app, o = {}) {
  const el = h('div', { class: 'ts-blocks' });
  let last = null;
  const render = (s) => {
    const blocks = s.me?.blocks ?? [];
    if (blocks === last) return;
    last = blocks;
    if (!blocks.length) { el.replaceChildren(h('p', { class: 'ts-empty' }, 'Пока нет постоянных блоков.')); return; }
    el.replaceChildren(h('ul', { class: 'ts-block-list' }, blocks.map((b) => {
      const info = [h('span', { class: 'ts-block-title' }, b.title), h('span', { class: 'ts-block-meta' }, `${fmtDays(b.days)}, ${fmtBlockTime(b)}`)];
      return h('li', { class: 'ts-block-item', dataset: { blockId: b.id, kind: b.kind, category: b.category ?? '' } },
        o.onEdit ? h('button', { type: 'button', class: 'ts-block-open', onClick: () => o.onEdit(b) }, info) : h('div', { class: 'ts-block-info' }, info),
        o.onEdit ? null : button('Удалить', { action: 'delete', onClick: () => app.actions.deleteBlock(b.id), attrs: { 'aria-label': `Удалить «${b.title}»` } }));
    })));
  };
  const unsub = app.subscribe(render);
  return { el, destroy: unsub };
}

/**
 * Block form. New block: presets + «Добавить». Existing block: every change saves at once, plus «Удалить блок».
 * <form class="ts-block-form" data-mode="new|edit">
 */
export function blockForm(app, { block = null, onSaved, onDeleted, submitVariant = 'primary' } = {}) {
  const editing = !!block;
  let draft = { ...(block ?? BLOCK_PRESETS[0].block) };
  const title = textInput({ value: draft.title, placeholder: 'Например, «Работа»', enterkeyhint: 'done' });
  const titleField = field('Название', title);
  const days = weekdayChips(draft.days, (d) => { draft.days = d; autosave(); });
  const start = timeInput(draft.start, { step: 5 });
  const end = timeInput(draft.end, { step: 5 });
  const nextDay = h('span', { class: 'ts-range-next-day', hidden: true }, '+1 день');
  const cat = categoryPicker(app.state.me, draft.category, (k) => { draft.category = k; autosave(); }, { noneLabel: 'Без категории' });
  const error = h('p', { class: 'ts-field-error', role: 'alert', hidden: true });
  const presetChips = editing ? null : h('div', { class: 'ts-chips', dataset: { kind: 'preset' }, role: 'group', 'aria-label': 'Шаблон' },
    BLOCK_PRESETS.map((p) => chip(p.label, { pressed: p.key === 'work', data: { preset: p.key }, onClick: () => applyPreset(p) })));
  const submit = editing ? null : button('Добавить', { variant: submitVariant, type: 'submit', action: 'add' });
  const del = editing ? button('Удалить блок', { variant: 'danger', action: 'delete', onClick: async () => { const r = await app.actions.deleteBlock(block.id); if (r.ok) onDeleted?.(); } }) : null;
  const form = h('form', { class: 'ts-block-form', dataset: { mode: editing ? 'edit' : 'new' }, novalidate: true },
    presetChips, titleField.el, h('div', { class: 'ts-field' }, h('span', { class: 'ts-field-label' }, 'Дни'), days.el),
    h('div', { class: 'ts-range-row' }, field('С', start).el, h('label', { class: 'ts-field' }, h('span', { class: 'ts-field-label' }, 'До'), end, nextDay)),
    cat.el, error, submit, del);

  function readTimes() {
    draft.start = start.value;
    draft.end = end.value;
    nextDay.hidden = !(draft.start && draft.end && T.parseClock(draft.end) <= T.parseClock(draft.start));
  }
  function applyPreset(p) {
    draft = { ...p.block };
    title.value = draft.title;
    days.set(draft.days);
    start.value = draft.start;
    end.value = draft.end;
    cat.set(draft.category);
    readTimes();
    for (const c of presetChips.children) c.setAttribute('aria-pressed', String(c.dataset.preset === p.key));
    if (!draft.title) title.focus();
  }
  function validate() {
    draft.title = title.value.trim();
    readTimes();
    let msg = null;
    if (!draft.title) msg = 'Назови блок.';
    else if (!draft.days.length) msg = 'Выбери хотя бы один день.';
    else if (!draft.start || !draft.end || draft.start === draft.end) msg = 'Укажи время начала и конца.';
    error.hidden = !msg;
    setText(error, msg ?? '');
    return !msg;
  }
  let saveTimer = 0;
  function autosave() {
    if (!editing) return;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(async () => {
      if (!validate()) return;
      const { id, ...body } = draft;
      const r = await app.actions.updateBlock(block.id, body);
      if (!r.ok) { error.hidden = false; setText(error, r.error?.message ?? 'Не сохранилось.'); } else onSaved?.(r.result);
    }, 350);
  }
  title.addEventListener('change', autosave);
  start.addEventListener('change', () => { readTimes(); autosave(); });
  end.addEventListener('change', () => { readTimes(); autosave(); });
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (editing) { title.blur(); return; }
    if (!validate()) return;
    submit.disabled = true;
    const { id, ...body } = draft;
    const r = await app.actions.addBlock(body);
    submit.disabled = false;
    if (r.ok) onSaved?.(r.result);
    else { error.hidden = false; setText(error, r.error?.message ?? 'Не получилось добавить.'); }
  });
  readTimes();
  return {
    el: form,
    reset() { applyPreset(BLOCK_PRESETS[2]); error.hidden = true; },
    destroy() { clearTimeout(saveTimer); },
  };
}
