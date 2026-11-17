// Voice input: record with MediaRecorder, then re-encode on the device to WAV 16 kHz mono 16-bit. The server needs
// no FFmpeg and never sees the browser's own container (Safari records AAC in MP4, Chrome Opus in WebM).

export const VOICE_RATE = 16000;

const audioContextClass = () => window.AudioContext || window.webkitAudioContext;
const offlineContextClass = () => window.OfflineAudioContext || window.webkitOfflineAudioContext;

/** The browser can record and re-encode: a secure context with a microphone API, MediaRecorder and Web Audio. */
export function voiceSupported() {
  return !!(navigator.mediaDevices?.getUserMedia && window.MediaRecorder && audioContextClass() && offlineContextClass());
}

export class VoiceError extends Error {
  /**
   * @param {'denied'|'no-device'|'busy'|'empty'|'decode'|'unsupported'} code
   * @param {string} message Russian text for the user
   */
  constructor(code, message) {
    super(message);
    this.name = 'VoiceError';
    this.code = code;
  }
}

function microphoneError(e) {
  switch (e?.name) {
    case 'NotAllowedError':
    case 'SecurityError':
      return new VoiceError('denied', 'Нет доступа к микрофону. Разреши его в настройках браузера.');
    case 'NotFoundError':
    case 'OverconstrainedError':
      return new VoiceError('no-device', 'Микрофон не найден.');
    case 'NotReadableError':
    case 'AbortError':
      return new VoiceError('busy', 'Микрофон занят другим приложением.');
    default:
      return new VoiceError('unsupported', 'Не получилось включить микрофон.');
  }
}

/** PCM samples in [-1, 1] → a WAV file (16-bit, mono). */
export function encodeWav(samples, rate = VOICE_RATE) {
  const out = new DataView(new ArrayBuffer(44 + samples.length * 2));
  const ascii = (at, text) => { for (let i = 0; i < text.length; i++) out.setUint8(at + i, text.charCodeAt(i)); };
  ascii(0, 'RIFF');
  out.setUint32(4, 36 + samples.length * 2, true);
  ascii(8, 'WAVEfmt ');
  out.setUint32(16, 16, true);
  out.setUint16(20, 1, true); // PCM
  out.setUint16(22, 1, true); // mono
  out.setUint32(24, rate, true);
  out.setUint32(28, rate * 2, true);
  out.setUint16(32, 2, true);
  out.setUint16(34, 16, true);
  ascii(36, 'data');
  out.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    const v = Math.max(-1, Math.min(1, samples[i] || 0));
    out.setInt16(44 + i * 2, Math.round(v < 0 ? v * 32768 : v * 32767), true);
  }
  return out.buffer;
}

/**
 * Channels at one rate → mono at another, in script: the average of the source window when going down (which also
 * low-passes), linear interpolation when going up. Good enough for speech; used where the browser cannot do it.
 * @param {Float32Array[]} channels
 */
export function resampleMono(channels, fromRate, toRate = VOICE_RATE) {
  const length = channels[0]?.length ?? 0;
  const at = (i) => { let sum = 0; for (const c of channels) sum += c[i]; return sum / channels.length; };
  const out = new Float32Array(Math.floor(length * toRate / fromRate));
  const step = fromRate / toRate;
  for (let i = 0; i < out.length; i++) {
    const from = i * step;
    if (step > 1) {
      const start = Math.floor(from);
      const end = Math.min(length, Math.max(start + 1, Math.floor((i + 1) * step)));
      let sum = 0;
      for (let j = start; j < end; j++) sum += at(j);
      out[i] = sum / (end - start);
    } else {
      const left = Math.min(Math.floor(from), length - 1);
      const right = Math.min(left + 1, length - 1);
      out[i] = at(left) + (at(right) - at(left)) * (from - left);
    }
  }
  return out;
}

async function to16k(buffer) {
  try {
    const offline = new (offlineContextClass())(1, Math.max(1, Math.round(buffer.duration * VOICE_RATE)), VOICE_RATE);
    const source = offline.createBufferSource();
    source.buffer = buffer;
    source.connect(offline.destination);
    source.start(0);
    const rendered = await new Promise((resolve, reject) => {
      offline.oncomplete = (e) => resolve(e.renderedBuffer); // older WebKit has no promise here
      const p = offline.startRendering();
      if (p?.then) p.then(resolve, reject);
    });
    return rendered.getChannelData(0);
  } catch {
    // An offline context at 16 kHz is refused by older WebKit.
    const channels = [];
    for (let c = 0; c < buffer.numberOfChannels; c++) channels.push(buffer.getChannelData(c));
    return resampleMono(channels, buffer.sampleRate, VOICE_RATE);
  }
}

/**
 * A recording in whatever the browser produced → WAV 16 kHz mono 16-bit, cut to maxSec.
 * @param {Blob} blob
 * @returns {Promise<{ wav: Blob, seconds: number }>}
 */
export async function toWav(blob, maxSec = Infinity) {
  const encoded = await new Response(blob).arrayBuffer();
  const context = new (audioContextClass())();
  let decoded;
  try {
    decoded = await new Promise((resolve, reject) => {
      const p = context.decodeAudioData(encoded, resolve, reject); // callbacks: older WebKit has no promise here
      if (p?.catch) p.catch(reject);
    });
  } catch {
    throw new VoiceError('decode', 'Не получилось обработать запись. Попробуй ещё раз или напиши текстом.');
  } finally {
    try { context.close()?.catch?.(() => {}); } catch { /* already closed */ }
  }
  if (!decoded?.length) throw new VoiceError('empty', 'Запись пустая. Попробуй ещё раз.');
  let samples = await to16k(decoded);
  const limit = Math.floor(maxSec * VOICE_RATE);
  if (samples.length > limit) samples = samples.subarray(0, limit);
  return { wav: new Blob([encodeWav(samples, VOICE_RATE)], { type: 'audio/wav' }), seconds: samples.length / VOICE_RATE };
}

/**
 * One recording at a time. start() asks for the microphone and resolves true once recording runs (false when it was
 * cancelled meanwhile, e.g. during the permission prompt); stop() resolves with the WAV; cancel() drops everything.
 * The microphone is held only while recording.
 * @param {{ maxSec?: number, onLimit?: () => void }} opts onLimit fires when maxSec is reached — call stop() there
 */
export function createRecorder(opts = {}) {
  const maxSec = opts.maxSec ?? 120;
  let state = 'idle'; // idle | starting | recording | stopping
  let stream = null;
  let recorder = null;
  let chunks = [];
  let timer = 0;
  let startedAt = 0;

  const release = () => {
    clearTimeout(timer);
    for (const track of stream?.getTracks() ?? []) track.stop();
    stream = null;
    recorder = null;
    state = 'idle';
  };

  return {
    get state() { return state; },
    /** Seconds since recording started. */
    get elapsed() { return state === 'recording' ? (performance.now() - startedAt) / 1000 : 0; },
    async start() {
      if (state !== 'idle') return false;
      if (!voiceSupported()) throw new VoiceError('unsupported', 'Этот браузер не умеет записывать голос.');
      state = 'starting';
      let granted;
      try {
        granted = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
      } catch (e) {
        if (state === 'starting') state = 'idle';
        throw microphoneError(e);
      }
      if (state !== 'starting') { // cancelled while the browser was asking for permission
        for (const track of granted.getTracks()) track.stop();
        return false;
      }
      try {
        stream = granted;
        recorder = new MediaRecorder(stream);
        chunks = [];
        recorder.addEventListener('dataavailable', (e) => { if (e.data?.size) chunks.push(e.data); });
        recorder.start();
      } catch (e) {
        stream = granted;
        release();
        throw microphoneError(e);
      }
      startedAt = performance.now();
      state = 'recording';
      timer = setTimeout(() => opts.onLimit?.(), maxSec * 1000);
      return true;
    },
    async stop() {
      if (state !== 'recording') { this.cancel(); return null; }
      state = 'stopping';
      const r = recorder;
      const parts = chunks;
      try {
        await new Promise((resolve, reject) => {
          r.addEventListener('stop', resolve, { once: true });
          r.addEventListener('error', reject, { once: true });
          r.stop();
        });
      } catch {
        release();
        throw new VoiceError('decode', 'Запись прервалась. Попробуй ещё раз.');
      }
      release();
      const blob = new Blob(parts, { type: r.mimeType || parts[0]?.type || '' });
      if (!blob.size) throw new VoiceError('empty', 'Запись пустая. Попробуй ещё раз.');
      return toWav(blob, maxSec);
    },
    cancel() {
      if (state === 'idle') return;
      try { if (recorder && recorder.state !== 'inactive') recorder.stop(); } catch { /* not started */ }
      release();
    },
  };
}
