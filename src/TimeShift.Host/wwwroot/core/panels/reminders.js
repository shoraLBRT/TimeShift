// Reminders: push on this device, interval, quiet hours, ping options.
import { h, setText } from '../dom.js';
import * as T from '../time.js';
import { button, chip, field, switchInput, note } from './common.js';

export const INTERVAL_PRESETS = [15, 30, 45, 60, 90, 120];

/** Push controls for this device: <div class="ts-push" data-state="unsupported|install|denied|off|on"> */
export function pushControls(app) {
  const el = h('div', { class: 'ts-push' });
  let key = '';
  const render = (s) => {
    const p = s.push;
    const k = JSON.stringify([p, s.busy === 'push', s.install?.kind]);
    if (k === key) return;
    key = k;
    let stateName;
    const parts = [];
    if (p.needsInstall) {
      stateName = 'install';
      parts.push(note('На iPhone уведомления работают только из приложения на экране «Домой». Нажми «Поделиться» → «На экран „Домой“», открой TimeShift оттуда и включи уведомления здесь.', 'install'));
    } else if (!p.supported) {
      stateName = 'unsupported';
      parts.push(note('Этот браузер не умеет показывать уведомления от сайта.', 'warning'));
    } else if (p.permission === 'denied') {
      stateName = 'denied';
      parts.push(note('Уведомления запрещены для этого сайта. Разреши их в настройках браузера и вернись сюда.', 'warning'));
    } else {
      stateName = p.subscribed ? 'on' : 'off';
      const sw = switchInput('Уведомления на этом устройстве', p.subscribed, (on) => {
        // change is a user gesture: enablePush asks for permission synchronously.
        if (on) app.actions.enablePush(); else app.actions.disablePush();
      }, { disabled: s.busy === 'push' });
      parts.push(sw.el);
      if (p.subscribed) parts.push(button('Проверить', { action: 'test-push', onClick: () => app.actions.testPush() }));
    }
    el.dataset.state = stateName;
    el.replaceChildren(...parts);
  };
  return { el, destroy: app.subscribe(render) };
}

/** Quiet hours: 1 h / 2 h / until morning, and cancel. <div class="ts-quiet" data-active> */
export function quietControls(app) {
  const status = h('p', { class: 'ts-quiet-status', 'aria-live': 'polite' });
  const cancel = button('Снять тишину', { action: 'quiet-off', onClick: () => app.actions.quiet(0) });
  const untilMorning = () => {
    const me = app.state.me;
    const tz = me?.timeZone ?? T.deviceTimeZone();
    const now = app.nowMs();
    const wake = T.parseClock(me?.sleep?.end ?? '08:00');
    const nowMin = T.nowMinIn(tz, now);
    let d = wake - nowMin;
    if (d <= 0) d += 1440;
    return Math.max(5, Math.min(1440, d));
  };
  const presets = h('div', { class: 'ts-chips', dataset: { kind: 'quiet' }, role: 'group', 'aria-label': 'Тишина' },
    chip('1 час', { data: { minutes: 60 }, onClick: () => app.actions.quiet(60) }),
    chip('2 часа', { data: { minutes: 120 }, onClick: () => app.actions.quiet(120) }),
    chip('До утра', { data: { minutes: 'morning' }, onClick: () => app.actions.quiet(untilMorning()) }));
  const el = h('div', { class: 'ts-quiet' }, status, presets, cancel);
  const render = (s) => {
    const until = s.me?.quietUntilUtc ? Date.parse(s.me.quietUntilUtc) : 0;
    const active = until > app.nowMs();
    el.dataset.active = String(active);
    cancel.hidden = !active;
    setText(status, active ? `Тишина до ${T.fmtClock(T.nowMinIn(s.me.timeZone, until))}` : 'Напоминания включены');
    for (const c of presets.children) c.disabled = !!s.busy;
  };
  return { el, destroy: app.subscribe(render) };
}

/** Interval: presets + a free value 5–240. */
export function intervalControls(app) {
  const lim = () => app.state.me?.limits?.pingIntervalMin ?? { min: 5, max: 240 };
  const input = h('input', { type: 'number', class: 'ts-input ts-number', inputmode: 'numeric', min: lim().min, max: lim().max, step: 1, enterkeyhint: 'done' });
  const f = field('Свой интервал, мин', input);
  const chips = h('div', { class: 'ts-chips', dataset: { kind: 'interval' }, role: 'group', 'aria-label': 'Интервал' },
    INTERVAL_PRESETS.map((m) => chip(T.fmtDuration(m), { data: { minutes: m }, onClick: () => save(m) })));
  const save = async (m) => {
    const { min, max } = lim();
    if (!Number.isInteger(m) || m < min || m > max) { f.setError(`От ${min} до ${max} минут.`); return; }
    f.setError(null);
    const r = await app.actions.saveSettings({ pingIntervalMin: m }, { inlineErrors: true });
    if (!r.ok) f.setError(r.error.message);
  };
  input.addEventListener('change', () => save(Number(input.value)));
  const render = (s) => {
    const v = s.me?.settings?.pingIntervalMin;
    if (document.activeElement !== input) input.value = v ?? '';
    for (const c of chips.children) c.setAttribute('aria-pressed', String(Number(c.dataset.minutes) === v));
  };
  const el = h('div', { class: 'ts-interval' }, h('span', { class: 'ts-field-label' }, 'Спрашивать каждые'), chips, f.el);
  return { el, destroy: app.subscribe(render) };
}

/** Stepper for an integer setting: <div class="ts-stepper"><button>−</button><output><button>+</button></div> */
export function stepper(app, key, { label, min, max, step = 1, format = (v) => String(v) }) {
  const out = h('output', { class: 'ts-stepper-value', 'aria-live': 'polite' });
  const dec = h('button', { type: 'button', class: 'ts-stepper-dec', 'aria-label': 'Меньше' }, '−');
  const inc = h('button', { type: 'button', class: 'ts-stepper-inc', 'aria-label': 'Больше' }, '+');
  const val = () => app.state.me?.settings?.[key] ?? min;
  const set = (v) => app.actions.saveSettings({ [key]: Math.max(min, Math.min(max, v)) });
  dec.addEventListener('click', () => set(val() - step));
  inc.addEventListener('click', () => set(val() + step));
  const el = h('div', { class: 'ts-field ts-stepper-field' }, h('span', { class: 'ts-field-label' }, label), h('div', { class: 'ts-stepper', role: 'group', 'aria-label': label }, dec, out, inc));
  const render = () => { const v = val(); setText(out, format(v)); dec.disabled = v <= min; inc.disabled = v >= max; };
  return { el, destroy: app.subscribe(render) };
}

/** The whole «Напоминания» page content. */
export function remindersContent(app) {
  const push = pushControls(app);
  const interval = intervalControls(app);
  const quiet = quietControls(app);
  const during = switchInput('Спрашивать во время блоков', !!app.state.me?.settings?.pingDuringBlocks,
    (on) => app.actions.saveSettings({ pingDuringBlocks: on }), { hint: 'Работа и учёба по расписанию — обычно не нужно.' });
  const lim = app.state.me?.limits ?? {};
  const unanswered = stepper(app, 'maxUnansweredPings', { label: 'Без ответа — пауза после', min: lim.maxUnansweredPings?.min ?? 1, max: lim.maxUnansweredPings?.max ?? 10, format: (v) => `${v} ${T.plural(v, 'вопроса', 'вопросов', 'вопросов')}` });
  const evening = stepper(app, 'eveningReviewOffsetMin', { label: 'Вечерний разбор после начала сна', min: lim.eveningReviewOffsetMin?.min ?? 0, max: lim.eveningReviewOffsetMin?.max ?? 180, step: 15, format: (v) => (v ? `через ${T.fmtDuration(v)}` : 'сразу') });
  const unsub = app.subscribe((s) => during.set(!!s.me?.settings?.pingDuringBlocks));
  const el = h('div', { class: 'ts-reminders' },
    h('section', { class: 'ts-section', dataset: { section: 'push' } }, h('h3', { class: 'ts-section-title' }, 'Это устройство'), push.el),
    h('section', { class: 'ts-section', dataset: { section: 'interval' } }, h('h3', { class: 'ts-section-title' }, 'Интервал'), interval.el),
    h('section', { class: 'ts-section', dataset: { section: 'quiet' } }, h('h3', { class: 'ts-section-title' }, 'Тишина'), quiet.el),
    h('section', { class: 'ts-section', dataset: { section: 'options' } }, h('h3', { class: 'ts-section-title' }, 'Когда спрашивать'), during.el, unanswered.el, evening.el));
  return { el, destroy() { push.destroy(); interval.destroy(); quiet.destroy(); unanswered.destroy(); evening.destroy(); unsub(); } };
}
