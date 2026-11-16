// Login: one field for the access key.
import { h, setText } from '../dom.js';
import { button, field } from './common.js';

export function loginPanel(app) {
  const input = h('input', { type: 'password', class: 'ts-input', name: 'key', autocomplete: 'current-password', required: true, enterkeyhint: 'go', autocapitalize: 'off', spellcheck: 'false' });
  const f = field('Ключ доступа', input, { hint: 'Его задают в настройках сервера (Web:AccessKey). Вводится один раз на устройстве.' });
  const submit = button('Войти', { variant: 'primary', type: 'submit', action: 'login' });
  const status = h('p', { class: 'ts-login-status', 'aria-live': 'polite' });
  const form = h('form', { class: 'ts-login', dataset: { state: 'idle' }, novalidate: true },
    h('h1', { class: 'ts-login-title' }, 'TimeShift'),
    h('p', { class: 'ts-login-text' }, 'Учёт прожитого времени. Чтобы открыть свои данные, введи ключ доступа.'),
    f.el, submit, status);
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const key = input.value.trim();
    if (!key) { f.setError('Введи ключ.'); input.focus(); return; }
    f.setError(null);
    form.dataset.state = 'busy';
    submit.disabled = true;
    const r = await app.actions.login(key);
    submit.disabled = false;
    form.dataset.state = r.ok ? 'idle' : 'error';
    if (!r.ok) { input.select(); }
  });
  const unsub = app.subscribe((s) => {
    f.setError(s.loginError);
    setText(status, s.demo ? 'Демо: подойдёт любой ключ.' : '');
  });
  return { el: form, title: 'Вход', destroy: unsub };
}
