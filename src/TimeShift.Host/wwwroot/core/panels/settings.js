// Settings: nested pages inside one sheet (push / pop). Every change saves at once, with rollback on error.
import { h, setText, prefersReducedMotion } from '../dom.js';
import * as T from '../time.js';
import { categoryInfo, categoriesByGroup, GROUPS } from '../timeline.js';
import { button, field, radioGroup, switchInput, timeInput, textInput, categoryPicker, note } from './common.js';
import { blockList, blockForm, fmtDays, fmtBlockTime } from './blocks.js';
import { remindersContent } from './reminders.js';
import { tzPicker } from './tz.js';
import { DESIGNS } from '../designs.js';

export const EXTENSION_PRESETS = [
  { key: 'typical', label: 'До типичной длительности', hint: 'Дальше — под вопросом', normFactor: 1.0, maxFactor: 1.0 },
  { key: 'default', label: '×1.5 норма, ×2 предел', hint: 'По умолчанию', normFactor: 1.5, maxFactor: 2.0 },
  { key: 'infinite', label: 'Пока не скажу', hint: 'Занятие длится, пока не остановишь', normFactor: null, maxFactor: null },
  { key: 'custom', label: 'Свой', hint: null },
];
export function extensionPreset(settings) {
  const p = EXTENSION_PRESETS.find((x) => x.key !== 'custom' && x.normFactor === settings?.normFactor && x.maxFactor === settings?.maxFactor);
  return p ?? EXTENSION_PRESETS[3];
}
const THEMES = [{ value: 'auto', label: 'Как в системе' }, { value: 'light', label: 'Светлая' }, { value: 'dark', label: 'Тёмная' }];

/**
 * @param {object} app
 * @param {{ close?: Function, page?: string }} opts  page opens a section directly ('schedule', 'reminders', …)
 */
export function settingsPanel(app, opts = {}) {
  const nav = h('div', { class: 'ts-nav' });
  const root = h('div', { class: 'ts-settings' }, nav);
  const stack = []; // { el, destroy, title, scroll }
  let sheet = null;

  const pageDuration = (el) => {
    const d = getComputedStyle(el).transitionDuration.split(',')[0];
    return d.endsWith('ms') ? parseFloat(d) : parseFloat(d) * 1000 || 0;
  };
  const after = (el, fn) => {
    let done = false;
    const run = () => { if (!done) { done = true; fn(); } };
    el.addEventListener('transitionend', (e) => { if (e.target === el) run(); }, { once: true });
    setTimeout(run, prefersReducedMotion() ? 0 : pageDuration(el) + 60);
  };
  const scroller = () => root.closest('.ts-sheet-body');

  function push(key, arg) {
    const def = PAGES[key](arg);
    const back = stack.length ? h('button', { type: 'button', class: 'ts-page-back', onClick: () => pop() }, 'Назад') : null;
    const el = h('section', { class: 'ts-page', dataset: { page: key, state: stack.length ? 'enter' : 'active' }, 'aria-label': def.title },
      stack.length ? h('header', { class: 'ts-page-header' }, back, h('h3', { class: 'ts-page-title' }, def.title)) : null,
      h('div', { class: 'ts-page-body' }, def.el));
    const prev = stack.at(-1);
    if (prev) prev.scroll = scroller()?.scrollTop ?? 0;
    const item = { el, destroy: def.destroy, title: def.title, entry: null };
    stack.push(item);
    // The system back (gesture / button) goes one page back instead of closing the sheet.
    if (prev && sheet?.pushHistoryEntry) item.entry = sheet.pushHistoryEntry(() => pop(true));
    nav.append(el);
    root.dataset.depth = String(stack.length);
    if (prev) {
      void el.offsetWidth;
      el.dataset.state = 'active';
      prev.el.dataset.state = 'below';
      after(el, () => { if (prev.el.dataset.state === 'below') prev.el.dataset.state = 'hidden'; });
      const sc = scroller();
      if (sc) sc.scrollTop = 0;
      back?.focus({ preventScroll: true });
    }
  }
  function pop(fromHistory = false) {
    if (stack.length < 2) return false;
    const top = stack.pop();
    if (!fromHistory) top.entry?.release();
    const prev = stack.at(-1);
    prev.el.dataset.state = 'below';
    void prev.el.offsetWidth;
    prev.el.dataset.state = 'active';
    top.el.dataset.state = 'leave';
    root.dataset.depth = String(stack.length);
    after(top.el, () => { top.el.remove(); top.destroy?.(); });
    const sc = scroller();
    if (sc) sc.scrollTop = prev.scroll ?? 0;
    prev.el.querySelector('.ts-page-back, .ts-menu-item')?.focus({ preventScroll: true });
    return true;
  }
  root.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && stack.length > 1) { e.preventDefault(); pop(); }
  });

  const ownCount = (me) => (me.categories ?? []).filter((c) => c.system === false && !c.archived).length;
  const section = (title, ...children) => h('section', { class: 'ts-section' }, title ? h('h4', { class: 'ts-section-title' }, title) : null, ...children);

  const PAGES = {
    root() {
      const items = [
        ['schedule', 'Расписание'], ['reminders', 'Напоминания'], ['tracking', 'Учёт'], ['activities', 'Занятия'], ['categories', 'Категории'], ['other', 'Прочее'],
      ].map(([key, label]) => {
        const value = h('span', { class: 'ts-menu-value' });
        const b = h('button', { type: 'button', class: 'ts-menu-item', dataset: { page: key }, onClick: () => push(key) }, h('span', { class: 'ts-menu-label' }, label), value);
        return { key, li: h('li', null, b), value };
      });
      const unsub = app.subscribe((s) => {
        const me = s.me;
        if (!me) return;
        const v = {
          schedule: `${me.sleep ? `Сон ${me.sleep.start}–${me.sleep.end}` : 'Сон не указан'}, ${me.blocks.length} ${T.plural(me.blocks.length, 'блок', 'блока', 'блоков')}`,
          reminders: me.quietUntilUtc && Date.parse(me.quietUntilUtc) > app.nowMs() ? `Тишина до ${T.fmtClock(T.nowMinIn(me.timeZone, Date.parse(me.quietUntilUtc)))}` : me.settings.pingsEnabled === false ? 'Выключены' : `Каждые ${T.fmtDuration(me.settings.pingIntervalMin)}`,
          tracking: extensionPreset(me.settings).label,
          activities: `${s.activities.length}`,
          categories: ownCount(me) ? `${ownCount(me)} ${T.plural(ownCount(me), 'своя', 'свои', 'своих')}` : 'Стандартные',
          other: T.fmtTimeZone(me.timeZone),
        };
        for (const it of items) setText(it.value, v[it.key]);
      });
      return { title: 'Настройки', el: h('ul', { class: 'ts-menu' }, items.map((i) => i.li)), destroy: unsub };
    },

    schedule() {
      const me = app.state.me;
      const sl = me.sleep ?? { start: '00:00', end: '08:00', weekend: null };
      const start = timeInput(sl.start);
      const end = timeInput(sl.end);
      const wStart = timeInput(sl.weekend?.start ?? '01:00');
      const wEnd = timeInput(sl.weekend?.end ?? '10:00');
      const wBox = h('div', { class: 'ts-range-row ts-sleep-weekend', hidden: !sl.weekend }, field('Ложусь', wStart).el, field('Встаю', wEnd).el);
      const sw = switchInput('В выходные по-другому', !!sl.weekend, (on) => { wBox.hidden = !on; saveSleep(); });
      const saveSleep = () => {
        if (!start.value || !end.value) return;
        app.actions.setSleep({ start: start.value, end: end.value, weekend: sw.input.checked && wStart.value && wEnd.value ? { start: wStart.value, end: wEnd.value } : null });
      };
      for (const i of [start, end, wStart, wEnd]) i.addEventListener('change', saveSleep);
      const list = blockList(app, { onEdit: (b) => push('block', b) });
      const add = button('Добавить блок', { action: 'add-block', onClick: () => push('block', null) });
      return {
        title: 'Расписание',
        el: h('div', null,
          section('Сон', h('div', { class: 'ts-range-row' }, field('Ложусь', start).el, field('Встаю', end).el), sw.el, wBox),
          section('Постоянные блоки', list.el, add)),
        destroy: list.destroy,
      };
    },

    block(block) {
      const form = blockForm(app, { block, onSaved: () => { if (!block) pop(); }, onDeleted: () => pop() });
      return { title: block ? block.title : 'Новый блок', el: form.el, destroy: form.destroy };
    },

    reminders() {
      const c = remindersContent(app);
      return { title: 'Напоминания', el: c.el, destroy: c.destroy };
    },

    tracking() {
      const s = app.state.me.settings;
      const norm = h('input', { type: 'number', class: 'ts-input ts-number', inputmode: 'decimal', min: '0.1', step: '0.1', value: s.normFactor ?? '' });
      const max = h('input', { type: 'number', class: 'ts-input ts-number', inputmode: 'decimal', min: '0.1', step: '0.1', value: s.maxFactor ?? '', placeholder: '∞' });
      const normF = field('Норма, × типичной', norm);
      const maxF = field('Предел, × типичной', max, { hint: 'Пусто — без предела' });
      const custom = h('div', { class: 'ts-extension-custom', hidden: extensionPreset(s).key !== 'custom' }, normF.el, maxF.el);
      const saveCustom = async () => {
        const n = norm.value === '' ? null : Number(norm.value);
        const m = max.value === '' ? null : Number(max.value);
        normF.setError(null); maxF.setError(null);
        if (n !== null && !(n > 0)) { normF.setError('Больше нуля.'); return; }
        if (n === null && m !== null) { maxF.setError('Без нормы предел не нужен.'); return; }
        if (m !== null && n !== null && m < n) { maxF.setError('Не меньше нормы.'); return; }
        const r = await app.actions.saveSettings({ normFactor: n, maxFactor: m }, { inlineErrors: true });
        if (!r.ok && r.error.status === 422) maxF.setError(r.error.message);
      };
      norm.addEventListener('change', saveCustom);
      max.addEventListener('change', saveCustom);
      const ext = radioGroup('Продление занятия', EXTENSION_PRESETS.map((p) => ({ value: p.key, label: p.label, hint: p.hint })), extensionPreset(s).key, (key) => {
        const p = EXTENSION_PRESETS.find((x) => x.key === key);
        custom.hidden = key !== 'custom';
        if (key !== 'custom') app.actions.saveSettings({ normFactor: p.normFactor, maxFactor: p.maxFactor });
        else norm.focus();
      });
      const rounding = radioGroup('Округление', (app.state.me.limits?.roundingMin ?? [1, 5, 10, 15]).map((m) => ({ value: m, label: `${m} мин` })), s.roundingMin,
        (m) => app.actions.saveSettings({ roundingMin: Number(m) }));
      const unsub = app.subscribe((st) => {
        const cur = st.me?.settings;
        if (!cur) return;
        if (custom.hidden) ext.set(extensionPreset(cur).key);
        rounding.set(cur.roundingMin);
        if (document.activeElement !== norm) norm.value = cur.normFactor ?? '';
        if (document.activeElement !== max) max.value = cur.maxFactor ?? '';
      });
      return {
        title: 'Учёт',
        el: h('div', null,
          section('Продление занятия', h('p', { class: 'ts-hint' }, 'Если не сказал «закончил», занятие продлевается само: до нормы — как предположение, до предела — под вопросом.'), ext.el, custom),
          section('Округление границ', rounding.el)),
        destroy: unsub,
      };
    },

    activities() {
      const list = h('ul', { class: 'ts-activity-list' });
      const empty = h('p', { class: 'ts-empty' }, 'Занятия появятся, когда начнёшь их записывать.');
      let last = null;
      const unsub = app.subscribe((s) => {
        if (s.activities === last) return;
        last = s.activities;
        empty.hidden = s.activities.length > 0;
        list.replaceChildren(...s.activities.map((a) => {
          const c = categoryInfo(a.category, s.me);
          const rules = a.rules?.length ? 'по времени суток' : null;
          return h('li', { class: 'ts-activity-item', dataset: { activityId: a.id, group: c.group } },
            h('button', { type: 'button', class: 'ts-activity-open', onClick: () => push('activity', a.id) },
              h('span', { class: 'ts-activity-name' }, a.name),
              h('span', { class: 'ts-activity-meta' }, [c.name, rules, a.typicalMin ? T.fmtDuration(a.typicalMin) : null].filter(Boolean).join(' · '))));
        }));
      });
      return { title: 'Занятия', el: h('div', null, list, empty), destroy: unsub };
    },

    activity(id) {
      const a = app.state.activities.find((x) => x.id === id);
      if (!a) return { title: 'Занятие', el: note('Занятие не найдено.') };
      const name = h('input', { type: 'text', class: 'ts-input', value: a.name, maxlength: 60, enterkeyhint: 'done', autocomplete: 'off' });
      const nameF = field('Название', name);
      name.addEventListener('change', async () => {
        const v = name.value.trim();
        if (!v) { nameF.setError('Название не может быть пустым.'); name.value = a.name; return; }
        nameF.setError(null);
        const r = await app.actions.updateActivity(id, { name: v });
        if (!r.ok) name.value = app.state.activities.find((x) => x.id === id)?.name ?? a.name;
      });
      const cat = categoryPicker(app.state.me, a.category, (k) => app.actions.updateActivity(id, { category: k }));
      const typ = h('input', { type: 'number', class: 'ts-input ts-number', inputmode: 'numeric', min: '1', max: '1440', step: '5', value: a.typicalMin ?? '', placeholder: 'не знаю' });
      const typF = field('Обычно длится, мин', typ, { hint: 'От неё считается продление занятия.' });
      typ.addEventListener('change', () => {
        const v = typ.value === '' ? null : Number(typ.value);
        if (v !== null && !(Number.isInteger(v) && v >= 1 && v <= 1440)) { typF.setError('От 1 до 1440 минут.'); return; }
        typF.setError(null);
        app.actions.updateActivity(id, { typicalMin: v });
      });
      const clear = button('Не знаю', { action: 'clear-typical', onClick: () => { typ.value = ''; typ.dispatchEvent(new Event('change')); } });
      // Classification rules (spec §4.2): a category of its own for a part of the day; "Как обычно" removes the rule.
      const ruleOf = (act, part) => act?.rules?.find((r) => r.part === part)?.category ?? null;
      const rules = T.PARTS_OF_DAY.map((p) => {
        const picker = categoryPicker(app.state.me, ruleOf(a, p.part), (k) => app.actions.setActivityRule(id, p.part, k),
          { caption: `${p.label}, ${p.hours}`, noneLabel: 'Как обычно' });
        picker.el.dataset.part = p.part;
        return { part: p.part, picker };
      });
      const unsub = app.subscribe((s) => {
        const cur = s.activities.find((x) => x.id === id);
        for (const r of rules) if (r.picker.get() !== ruleOf(cur, r.part)) r.picker.set(ruleOf(cur, r.part));
      });
      const byTime = h('section', { class: 'ts-section ts-activity-rules' },
        h('h4', { class: 'ts-section-title' }, 'По времени суток'),
        h('p', { class: 'ts-hint' }, 'Если в какое-то время суток это занятие — другое: например, YouTube вечером — отдых. Считается по началу записи и действует на новые записи; записанное раньше не меняется.'),
        rules.map((r) => r.picker.el));
      return { title: a.name, el: h('div', { class: 'ts-activity-form' }, nameF.el, cat.el, typF.el, clear, byTime), destroy: unsub };
    },

    // Own categories (spec §4.1): the standard set is fixed, own ones are added, renamed, moved and deleted here.
    // The markup is the list and the form the other pages use, so a design needs no extra styles for it.
    categories() {
      const body = h('div', { class: 'ts-categories' });
      // One flat run of sections (intro, a section per group, the button), so a design's own gap between sections applies.
      const intro = section(null, h('p', { class: 'ts-hint' }, 'Своя категория появляется в выборе при записи и считается в итогах дня и недели. Стандартные изменить нельзя.'));
      const add = section(null, button('Добавить категорию', { action: 'add-category', onClick: () => push('category', null) }));
      let last = null;
      const unsub = app.subscribe((s) => {
        if (!s.me || s.me.categories === last) return;
        last = s.me.categories;
        body.replaceChildren(intro, ...categoriesByGroup(s.me).map((g) => {
          const names = (list) => list.map((c) => c.name).join(', ');
          const standard = g.categories.filter((c) => c.system && c.depth === 0)
            .map((c) => {
              const sub = g.categories.filter((x) => x.system && x.parent === c.key);
              return sub.length ? `${c.name} (${names(sub)})` : c.name;
            }).join(' · ');
          const own = g.categories.filter((c) => !c.system);
          return section(g.name,
            standard ? h('p', { class: 'ts-hint' }, standard) : null,
            own.length ? h('ul', { class: 'ts-activity-list' }, own.map((c) =>
              h('li', { class: 'ts-activity-item', dataset: { category: c.key, group: g.group } },
                h('button', { type: 'button', class: 'ts-activity-open', onClick: () => push('category', c.key) },
                  h('span', { class: 'ts-activity-name' }, c.name),
                  h('span', { class: 'ts-activity-meta' }, c.parent ? `Подвид: ${categoryInfo(c.parent, s.me).name}` : 'Своя'))))) : null);
        }), add);
      });
      return { title: 'Категории', el: body, destroy: unsub };
    },

    category(key) {
      const me = app.state.me;
      const cur = key ? me.categories.find((c) => c.key === key && c.system === false && !c.archived) : null;
      if (key && !cur) return { title: 'Категория', el: note('Категория не найдена.') };
      const draft = { name: cur?.name ?? '', group: cur?.group ?? 'useful', parent: cur?.parent ?? null };
      const stored = () => app.state.me.categories.find((c) => c.key === key) ?? draft;
      const name = textInput({ value: draft.name, placeholder: 'Например, «Музыка»', maxLength: 40, enterkeyhint: 'done' });
      const nameF = field('Название', name);
      const error = h('p', { class: 'ts-field-error', role: 'alert', hidden: true });
      const fail = (r) => {
        const e = r.error;
        if (e?.status === 422 && e.field === 'name') nameF.setError(e.message);
        else if (e?.status === 422) { error.hidden = false; setText(error, e.message); }
      };
      const clear = () => { nameF.setError(null); error.hidden = true; };
      // An existing category saves every change at once; a new one is sent by the button.
      const save = async (patch) => {
        if (!cur) return;
        clear();
        const r = await app.actions.updateCategory(key, patch, { inlineErrors: true });
        if (r.ok) return;
        fail(r);
        // Not saved: the form goes back to what the server has; the message says why.
        const c = stored();
        Object.assign(draft, { name: c.name, group: c.group, parent: c.parent ?? null });
        name.value = c.name;
        groups.set(c.group);
        parent?.set(c.parent ?? null);
      };
      const groups = radioGroup('Группа', GROUPS.filter((g) => g.key !== 'none').map((g) => ({ value: g.key, label: g.name })), draft.group, (g) => {
        draft.group = g;
        draft.parent = null;
        parent?.set(null);
        save({ group: g, parent: null });
      });
      // Two levels only: a category that has subcategories cannot become one itself.
      const hasChildren = !!cur && me.categories.some((c) => c.parent === key && !c.archived);
      const parent = hasChildren ? null : categoryPicker(me, draft.parent, (k) => {
        draft.parent = k;
        if (k) { draft.group = categoryInfo(k, me).group; groups.set(draft.group); }
        save(k ? { parent: k } : { parent: null, group: draft.group });
      }, { caption: 'Подвид категории', noneLabel: 'Нет, отдельная', filter: (c) => c.depth === 0 && c.key !== key });
      name.addEventListener('change', () => {
        const v = name.value.trim();
        if (!cur) return;
        if (!v) { nameF.setError('Название не может быть пустым.'); name.value = stored().name; return; }
        if (v !== stored().name) save({ name: v });
      });
      const submit = cur ? null : button('Добавить', { variant: 'primary', type: 'submit', action: 'add' });
      const del = cur ? button('Удалить категорию', { variant: 'danger', action: 'delete', onClick: async () => { const r = await app.actions.deleteCategory(key); if (r.ok) pop(); } }) : null;
      const form = h('form', { class: 'ts-block-form ts-category-form', dataset: { mode: cur ? 'edit' : 'new' }, novalidate: true },
        nameF.el,
        section('Группа', groups.el),
        parent?.el,
        h('p', { class: 'ts-hint' }, parent
          ? 'Подвид считается в итогах вместе со своей категорией и берёт её группу.'
          : 'У этой категории есть подвиды, поэтому сама она подвидом быть не может.'),
        error, submit, del,
        cur ? h('p', { class: 'ts-hint' }, 'После удаления прошлые записи останутся как были, а в выборе категории больше не будет. Вернуть её можно, добавив снова с тем же названием.') : null);
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (cur) { name.blur(); return; }
        clear();
        const v = name.value.trim();
        if (!v) { nameF.setError('Назови категорию.'); return; }
        submit.disabled = true;
        const r = await app.actions.addCategory(draft.parent ? { name: v, parent: draft.parent } : { name: v, group: draft.group }, { inlineErrors: true });
        submit.disabled = false;
        if (r.ok) pop(); else fail(r);
      });
      return { title: cur ? cur.name : 'Новая категория', el: form };
    },

    other() {
      const s = app.state;
      const tzValue = h('span', { class: 'ts-menu-value' }, T.fmtTimeZone(s.me.timeZone));
      const tzBtn = h('button', { type: 'button', class: 'ts-menu-item', dataset: { page: 'tz' }, onClick: () => push('tz') }, h('span', { class: 'ts-menu-label' }, 'Часовой пояс'), tzValue);
      const theme = radioGroup('Тема', THEMES, s.theme, (t) => app.actions.setTheme(t));
      const design = radioGroup('Вид', DESIGNS.map((d) => ({ value: d.id, label: d.name })), s.design, (id) => app.actions.switchDesign(id),
        { className: 'ts-designs' });
      const install = h('div', { class: 'ts-install' });
      const renderInstall = (info) => {
        install.dataset.kind = info.kind;
        install.replaceChildren(...[note(info.text, info.kind === 'installed' ? 'success' : 'install'),
          info.kind === 'prompt' ? button('Установить', { variant: 'primary', action: 'install', onClick: () => app.actions.promptInstall() }) : null].filter(Boolean));
      };
      let lastInstall = null;
      const unsub = app.subscribe((st) => {
        setText(tzValue, T.fmtTimeZone(st.me?.timeZone ?? 'UTC'));
        theme.set(st.theme);
        if (st.install !== lastInstall) { lastInstall = st.install; renderInstall(st.install); }
      });
      const version = [s.meta?.version ? `Версия ${s.meta.version}` : null, s.demo ? 'демо-режим' : null].filter(Boolean).join(' · ');
      return {
        title: 'Прочее',
        el: h('div', null,
          section(null, h('ul', { class: 'ts-menu' }, h('li', null, tzBtn))),
          section('Тема', theme.el),
          // Вид: the same radio-group markup as «Тема», so every design already styles it. Hidden when embedded.
          s.embed ? null : section('Вид', design.el),
          section('Установка', install),
          section('О приложении', h('p', { class: 'ts-version' }, version || 'TimeShift'),
            s.me?.telegramLinked ? h('p', { class: 'ts-hint' }, 'Связано с Telegram: записи общие.') : null),
          section(null, button('Выйти', { variant: 'danger', action: 'logout', onClick: () => { opts.close?.(); app.actions.logout(); } }))),
        destroy: unsub,
      };
    },

    tz() {
      const picker = tzPicker(app.state.me.timeZone, async (tz) => {
        const r = await app.actions.saveSettings({ timeZone: tz });
        if (r.ok) pop();
      });
      return { title: 'Часовой пояс', el: picker.el };
    },
  };

  push('root');
  if (opts.page && PAGES[opts.page]) push(opts.page);
  return {
    el: root,
    title: 'Настройки',
    onShow(handle) {
      sheet = handle;
      // Pages pushed before the sheet existed (opts.page) get their history step now.
      for (const it of stack.slice(1)) if (!it.entry && handle.pushHistoryEntry) it.entry = handle.pushHistoryEntry(() => pop(true));
    },  // the sheet handle, for designs that want it
    back: pop,
    destroy() { while (stack.length) stack.pop().destroy?.(); },
  };
}

export { fmtDays, fmtBlockTime };
