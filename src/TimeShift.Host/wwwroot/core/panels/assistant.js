// Free text: a phrase to the server's dialog engine and its replies with buttons. With speech recognition on the
// server the composer also has a hold-to-talk microphone.
import { h, reconcile, setText, setData } from '../dom.js';
import { button } from './common.js';
import { micControl, voiceAvailable } from './mic.js';

/**
 * @param {object} app
 * @param {{ text?: string, close?: Function }} opts  text is sent right away
 */
export function assistantPanel(app, opts = {}) {
  const thread = h('ol', { class: 'ts-thread', 'aria-live': 'polite', 'aria-label': 'Переписка' });
  const empty = h('div', { class: 'ts-thread-empty' },
    h('p', { class: 'ts-hint' }, 'Напиши, что делал, своими словами. Прошлое тоже можно:'),
    h('ul', { class: 'ts-examples' }, ['час тренировался, потом поехал домой', 'вчера 19-21 зал', 'с 14 до 15 обед с мамой'].map((t) =>
      h('li', null, h('button', { type: 'button', class: 'ts-example', onClick: () => { input.value = t; input.focus(); grow(); } }, t)))));
  const input = h('textarea', { class: 'ts-input ts-compose-input', rows: '1', maxlength: '500', placeholder: 'Что делал?', enterkeyhint: 'send', 'aria-label': 'Сообщение', autocapitalize: 'sentences' });
  const send = button('Отправить', { variant: 'primary', type: 'submit', action: 'send' });
  // The microphone exists only when the server can recognise speech and the browser can record.
  const mic = voiceAvailable(app)
    ? micControl(app, {
      onPhase: (phase, cancel) => {
        input.hidden = phase !== 'idle';
        setData(compose, 'voice', phase);
        setData(compose, 'voiceCancel', cancel ? 'true' : null);
      },
    })
    : null;
  const compose = h('form', { class: 'ts-compose', novalidate: true, dataset: { voice: mic ? 'idle' : null, hasText: 'false' } }, input, mic?.status, mic?.button, send);
  const root = h('section', { class: 'ts-assistant' }, empty, thread, compose);
  if (mic) empty.querySelector('.ts-hint').textContent = 'Напиши или скажи, что делал, своими словами. Прошлое тоже можно:';

  const grow = () => {
    input.style.height = 'auto';
    input.style.height = `${Math.min(input.scrollHeight, 160)}px`;
    // One slot on the right: the microphone while the field is empty, «Отправить» once there is text.
    setData(compose, 'hasText', input.value.trim() ? 'true' : 'false');
  };
  input.addEventListener('input', grow);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); compose.requestSubmit(); }
  });
  compose.addEventListener('submit', (e) => {
    e.preventDefault();
    const t = input.value.trim();
    if (!t || app.state.threadBusy) return;
    input.value = '';
    grow();
    app.actions.sendMessage(t);
  });

  const renderMsg = (el, m) => {
    setData(el, 'from', m.from);
    setData(el, 'pending', m.pending ? 'true' : null);
    setData(el, 'error', m.error ? 'true' : null);
    setText(el.querySelector('.ts-msg-text'), m.text);
    const err = el.querySelector('.ts-msg-error');
    err.hidden = !m.error;
    setText(err, m.error ?? '');
    const box = el.querySelector('.ts-msg-buttons');
    const key = JSON.stringify([m.buttons, m.pressed]);
    if (box.__key !== key) {
      box.__key = key;
      box.replaceChildren(...(m.buttons ?? []).map((row) => h('div', { class: 'ts-msg-row' }, row.map((b) =>
        h('button', { type: 'button', class: 'ts-msg-button', dataset: { data: b.data }, 'aria-pressed': m.pressed === b.data ? 'true' : null, disabled: !!m.pressed, onClick: () => app.actions.pressButton(b.data, m.id) }, b.label)))));
      box.hidden = !(m.buttons?.length);
    }
  };
  let lastThread = null;
  const unsub = app.subscribe((s) => {
    send.disabled = s.threadBusy;
    if (mic) mic.button.disabled = s.threadBusy;
    root.dataset.busy = s.threadBusy ? 'true' : 'false';
    if (s.thread === lastThread) return;
    lastThread = s.thread;
    empty.hidden = s.thread.length > 0;
    reconcile(thread, s.thread, {
      key: (m) => m.id,
      create: () => h('li', { class: 'ts-msg' }, h('p', { class: 'ts-msg-text' }), h('p', { class: 'ts-msg-error', role: 'alert', hidden: true }), h('div', { class: 'ts-msg-buttons', hidden: true })),
      update: renderMsg,
    });
    const body = root.closest('.ts-sheet-body');
    requestAnimationFrame(() => { if (body) body.scrollTop = body.scrollHeight; });
  });
  if (opts.text) app.actions.sendMessage(opts.text);
  return { el: root, title: 'Записать фразой', destroy: () => { unsub(); mic?.destroy(); }, focus: input };
}
