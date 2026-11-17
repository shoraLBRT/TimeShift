// Hold-to-talk: hold the microphone button and speak, release to send, slide away to cancel.
import { h, setText, setData } from '../dom.js';
import { showToast } from '../toast.js';
import { createRecorder, voiceSupported } from '../voice.js';

const MIN_HOLD_SEC = 0.6;      // shorter is a tap, not a phrase
const CANCEL_DISTANCE = 72;    // px the finger moves away from where it pressed
const clock = (sec) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`;

/** The server recognises speech (meta.speech) and this browser can record. */
export function voiceAvailable(app) {
  return !!app.state.meta?.speech && voiceSupported();
}

/**
 * @param {object} app
 * @param {{ onPhase?: (phase: 'idle'|'starting'|'recording'|'sending', cancelArmed: boolean) => void }} opts
 * @returns {{ button: HTMLButtonElement, status: HTMLElement, destroy(): void }}
 *   button — `button.ts-mic[data-phase]`; status — `p.ts-voice-status` (`.ts-voice-time`, `.ts-voice-hint`), shown
 *   while recording.
 */
export function micControl(app, opts = {}) {
  const maxSec = Math.max(5, Number(app.state.meta?.speechMaxSec) || 120);
  const time = h('span', { class: 'ts-voice-time' }, '0:00');
  const hint = h('span', { class: 'ts-voice-hint' });
  const status = h('p', { class: 'ts-voice-status', role: 'status', hidden: true }, time, hint);
  const button = h('button', { type: 'button', class: 'ts-mic', dataset: { action: 'voice', phase: 'idle' }, 'aria-label': 'Голос: удерживай и говори', title: 'Удерживай и говори' });

  let phase = 'idle';
  let held = false;
  let cancelArmed = false;
  let origin = null;
  let ticker = 0;
  const recorder = createRecorder({ maxSec, onLimit: () => finish(true) });

  const HINTS = { starting: 'Включаю микрофон…', recording: 'Говори. Отпусти — отправлю', armed: 'Отпусти — отмена', sending: 'Обрабатываю запись…' };
  function setPhase(next, armed = false) {
    phase = next;
    cancelArmed = armed;
    setData(button, 'phase', next);
    setData(button, 'cancel', armed ? 'true' : null);
    button.setAttribute('aria-pressed', next === 'recording' ? 'true' : 'false');
    status.hidden = next === 'idle';
    setData(status, 'cancel', armed ? 'true' : null);
    setText(hint, armed ? HINTS.armed : HINTS[next] ?? '');
    if (next !== 'recording') { clearInterval(ticker); ticker = 0; if (next !== 'sending') setText(time, '0:00'); }
    opts.onPhase?.(next, armed);
  }
  const say = (message) => showToast({ message, kind: 'info', duration: 4000 });
  const fail = (error) => showToast({ message: error?.message || 'Не получилось записать голос.', kind: 'error' });

  async function begin() {
    if (phase !== 'idle' || app.state.threadBusy) return;
    held = true;
    setPhase('starting');
    let running;
    try { running = await recorder.start(); } catch (e) { held = false; setPhase('idle'); fail(e); return; }
    if (!running || !held) {
      // Released before the microphone was ready — usually the permission prompt took the press away.
      recorder.cancel();
      setPhase('idle');
      say('Микрофон готов. Удерживай кнопку и говори.');
      return;
    }
    try { navigator.vibrate?.(12); } catch { /* not allowed */ }
    setPhase('recording');
    ticker = setInterval(() => setText(time, clock(recorder.elapsed)), 250);
  }

  /** @param {boolean} send false drops the recording */
  async function finish(send) {
    held = false;
    origin = null;
    if (phase === 'starting') { recorder.cancel(); return; } // begin() notices and explains
    if (phase !== 'recording') return;
    const elapsed = recorder.elapsed;
    if (!send || cancelArmed) { recorder.cancel(); setPhase('idle'); return; }
    if (elapsed < MIN_HOLD_SEC) { recorder.cancel(); setPhase('idle'); say('Удерживай кнопку, пока говоришь.'); return; }
    setPhase('sending');
    try {
      const result = await recorder.stop();
      setPhase('idle');
      if (result) app.actions.sendVoice(result.wav, result.seconds);
    } catch (e) { setPhase('idle'); fail(e); }
  }

  button.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || !e.isPrimary) return;
    e.preventDefault(); // keep the focus and the keyboard where they are; no text selection
    try { button.setPointerCapture(e.pointerId); } catch { /* pointer already gone */ }
    origin = { x: e.clientX, y: e.clientY };
    begin();
  });
  button.addEventListener('pointermove', (e) => {
    if (!origin || phase !== 'recording') return;
    const armed = Math.hypot(e.clientX - origin.x, e.clientY - origin.y) > CANCEL_DISTANCE;
    if (armed !== cancelArmed) setPhase('recording', armed);
  });
  button.addEventListener('pointerup', () => { if (held) finish(true); });
  button.addEventListener('pointercancel', () => { if (held) finish(false); });
  button.addEventListener('contextmenu', (e) => e.preventDefault());
  // Keyboard: hold Space or Enter; Escape cancels.
  button.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && held) { e.preventDefault(); e.stopPropagation(); finish(false); return; }
    if ((e.key === ' ' || e.key === 'Enter') && !e.repeat) { e.preventDefault(); begin(); } else if (e.key === ' ' || e.key === 'Enter') e.preventDefault();
  });
  button.addEventListener('keyup', (e) => { if ((e.key === ' ' || e.key === 'Enter') && held) { e.preventDefault(); finish(true); } });
  button.addEventListener('blur', () => { if (held && !origin) finish(false); });
  button.addEventListener('click', (e) => e.preventDefault());
  // The app going to the background mid-phrase: drop it rather than send half.
  const onHide = () => { if (document.hidden && held) finish(false); };
  document.addEventListener('visibilitychange', onHide);

  return {
    button, status,
    destroy() {
      document.removeEventListener('visibilitychange', onHide);
      held = false;
      clearInterval(ticker);
      recorder.cancel();
    },
  };
}
