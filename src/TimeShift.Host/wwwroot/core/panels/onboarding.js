// Onboarding: time zone → sleep → regular blocks → reminders → finish.
import { h, setText } from '../dom.js';
import * as T from '../time.js';
import { button, chip, field, timeInput, switchInput, note } from './common.js';
import { tzPicker } from './tz.js';
import { blockForm, blockList } from './blocks.js';
import { pushControls, INTERVAL_PRESETS } from './reminders.js';

export const ONBOARDING_STEPS = [
  { key: 'tz', label: 'Пояс' },
  { key: 'sleep', label: 'Сон' },
  { key: 'blocks', label: 'Расписание' },
  { key: 'reminders', label: 'Напоминания' },
  { key: 'done', label: 'Готово' },
];

export function onboardingPanel(app) {
  let step = 0;
  const cleanups = [];
  const me = () => app.state.me;
  // Draft values survive moving between steps.
  const deviceTz = T.deviceTimeZone();
  const draft = {
    tz: me()?.timeZone && me().timeZone !== 'UTC' ? me().timeZone : deviceTz,
    sleep: me()?.sleep ?? { start: '00:00', end: '08:00', weekend: null },
  };
  if (!me()?.timeZone || me().timeZone === 'UTC') draft.tz = deviceTz;

  const progress = h('div', { class: 'ts-progress', role: 'progressbar', 'aria-valuemin': '1', 'aria-valuemax': String(ONBOARDING_STEPS.length), 'aria-label': 'Шаг настройки' });
  const steps = h('ol', { class: 'ts-steps' }, ONBOARDING_STEPS.map((s) => h('li', { class: 'ts-step', dataset: { step: s.key } }, s.label)));
  const page = h('div', { class: 'ts-onboarding-page' });
  const back = button('Назад', { action: 'back', onClick: () => go(step - 1) });
  const next = button('Далее', { variant: 'primary', action: 'next', onClick: () => advance() });
  const nav = h('footer', { class: 'ts-onboarding-nav' }, back, next);
  const root = h('section', { class: 'ts-onboarding', 'aria-labelledby': 'ts-onb-title' },
    h('header', { class: 'ts-onboarding-header' }, progress, steps), page, nav);

  let leave = async () => true; // the current step's commit before moving on

  function go(i) {
    step = Math.max(0, Math.min(ONBOARDING_STEPS.length - 1, i));
    while (cleanups.length) cleanups.pop()();
    const s = ONBOARDING_STEPS[step];
    root.dataset.step = s.key;
    progress.setAttribute('aria-valuenow', String(step + 1));
    progress.setAttribute('aria-valuetext', `${step + 1} из ${ONBOARDING_STEPS.length}: ${s.label}`);
    progress.style.setProperty('--progress', String((step + 1) / ONBOARDING_STEPS.length));
    [...steps.children].forEach((li, j) => {
      if (j === step) li.setAttribute('aria-current', 'step'); else li.removeAttribute('aria-current');
      li.dataset.done = j < step ? 'true' : 'false';
    });
    back.hidden = step === 0;
    setText(next, step === ONBOARDING_STEPS.length - 1 ? 'Начать' : 'Далее');
    next.dataset.action = step === ONBOARDING_STEPS.length - 1 ? 'finish' : 'next';
    page.dataset.step = s.key;
    page.replaceChildren(...PAGES[s.key]());
    page.querySelector('.ts-onboarding-title')?.setAttribute('id', 'ts-onb-title');
    page.scrollTop = 0;
    root.scrollIntoView?.({ block: 'start' });
  }

  async function advance() {
    if (next.disabled) return;
    next.disabled = true;
    root.dataset.busy = 'true';
    try {
      const ok = await leave();
      if (!ok) return;
      if (step === ONBOARDING_STEPS.length - 1) await app.actions.completeOnboarding();
      else go(step + 1);
    } finally {
      next.disabled = false;
      delete root.dataset.busy;
    }
  }

  const title = (t, text) => [h('h2', { class: 'ts-onboarding-title' }, t), text ? h('p', { class: 'ts-onboarding-text' }, text) : null];

  const PAGES = {
    tz() {
      const current = h('p', { class: 'ts-tz-current' }, T.fmtTimeZone(draft.tz));
      let picker = null;
      const toggle = button('Другой пояс', { action: 'change-tz' });
      const holder = h('div', { class: 'ts-tz-holder' });
      toggle.addEventListener('click', () => {
        if (picker) { picker.el.remove(); picker = null; toggle.setAttribute('aria-expanded', 'false'); return; }
        picker = tzPicker(draft.tz, (tz) => { draft.tz = tz; setText(current, T.fmtTimeZone(tz)); });
        holder.append(picker.el);
        toggle.setAttribute('aria-expanded', 'true');
        picker.search.focus();
      });
      leave = async () => {
        if (me()?.timeZone === draft.tz) return true;
        return (await app.actions.saveSettings({ timeZone: draft.tz })).ok;
      };
      return [...title('Часовой пояс', 'Определён по устройству. Всё время хранится в UTC, а показывается в этом поясе.'), current, toggle, holder];
    },

    sleep() {
      const s = draft.sleep;
      const start = timeInput(s.start, { step: 5 });
      const end = timeInput(s.end, { step: 5 });
      const wStart = timeInput(s.weekend?.start ?? '01:00', { step: 5 });
      const wEnd = timeInput(s.weekend?.end ?? '10:00', { step: 5 });
      const weekendBox = h('div', { class: 'ts-sleep-weekend', hidden: !s.weekend },
        h('div', { class: 'ts-range-row' }, field('Ложусь', wStart).el, field('Встаю', wEnd).el));
      const sw = switchInput('В выходные по-другому', !!s.weekend, (on) => { weekendBox.hidden = !on; sync(); });
      const err = h('p', { class: 'ts-field-error', role: 'alert', hidden: true });
      const sync = () => {
        draft.sleep = { start: start.value, end: end.value, weekend: sw.input.checked ? { start: wStart.value, end: wEnd.value } : null };
      };
      for (const i of [start, end, wStart, wEnd]) i.addEventListener('change', sync);
      leave = async () => {
        sync();
        if (!draft.sleep.start || !draft.sleep.end || draft.sleep.start === draft.sleep.end) { err.hidden = false; setText(err, 'Укажи, когда ложишься и встаёшь.'); return false; }
        err.hidden = true;
        return (await app.actions.setSleep(draft.sleep)).ok;
      };
      return [...title('Сон', 'Ночь по шаблону ляжет в ленту сама — останется только подтвердить.'),
        h('div', { class: 'ts-range-row' }, field('Ложусь', start).el, field('Встаю', end).el), sw.el, weekendBox, err];
    },

    blocks() {
      const list = blockList(app, { onEdit: null });
      cleanups.push(list.destroy);
      const form = blockForm(app, { onSaved: () => form.reset() });
      cleanups.push(form.destroy);
      leave = async () => true;
      return [...title('Постоянные дела', 'Работа, учёба, свои регулярные блоки. Они предполагаются в ленте, пока ты не скажешь иначе. Можно пропустить.'),
        form.el, list.el];
    },

    reminders() {
      const cur = me()?.settings?.pingIntervalMin ?? 30;
      const chips = h('div', { class: 'ts-chips', role: 'group', 'aria-label': 'Интервал', dataset: { kind: 'interval' } },
        INTERVAL_PRESETS.map((m) => chip(T.fmtDuration(m), { pressed: m === cur, data: { minutes: m }, onClick: async () => {
          for (const c of chips.children) c.setAttribute('aria-pressed', String(Number(c.dataset.minutes) === m));
          await app.actions.saveSettings({ pingIntervalMin: m });
        } })));
      const push = pushControls(app);
      cleanups.push(push.destroy);
      leave = async () => true;
      return [...title('Напоминания', 'Как часто спрашивать «что делаешь?», если ничего не записано.'),
        h('div', { class: 'ts-field' }, h('span', { class: 'ts-field-label' }, 'Интервал'), chips), push.el];
    },

    done() {
      const m = me();
      const lines = [
        `Пояс: ${T.fmtTimeZone(m?.timeZone ?? draft.tz)}`,
        m?.sleep ? `Сон: ${m.sleep.start}–${m.sleep.end}${m.sleep.weekend ? `, в выходные ${m.sleep.weekend.start}–${m.sleep.weekend.end}` : ''}` : 'Сон не указан',
        `Блоков: ${m?.blocks?.length ?? 0}`,
        `Напоминать каждые ${T.fmtDuration(m?.settings?.pingIntervalMin ?? 30)}`,
      ];
      leave = async () => true;
      return [...title('Всё готово', 'Остальное можно поменять в настройках.'),
        h('ul', { class: 'ts-onboarding-summary' }, lines.map((l) => h('li', null, l))),
        !m?.sleep ? note('Без времени сна закончить нельзя — вернись на шаг «Сон».', 'warning') : null];
    },
  };

  go(0);
  return { el: root, title: 'Настройка', destroy() { while (cleanups.length) cleanups.pop()(); } };
}
