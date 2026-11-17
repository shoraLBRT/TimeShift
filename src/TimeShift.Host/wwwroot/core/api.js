// Live client for docs/web-api.md. Every method returns the parsed JSON (or null for 204) or throws ApiError.

export class ApiError extends Error {
  /**
   * @param {number} status HTTP status (0 when the request did not reach the server)
   * @param {string} code   server `error` code, or 'offline' / 'timeout' / 'http'
   * @param {string} message Russian text for the user
   * @param {string|null} field the invalid field for 422
   */
  constructor(status, code, message, field = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.field = field;
  }
}

const VOICE_TIMEOUT_MS = 180000;

const FALLBACK_MESSAGES = {
  0: 'Нет сети. Изменение не сохранено.',
  400: 'Сервер не понял запрос.',
  401: 'Нужно войти заново.',
  403: 'Запрос отклонён.',
  404: 'Не найдено — возможно, уже изменилось.',
  409: 'Не получилось: конфликт с тем, что уже есть.',
  422: 'Неверное значение.',
  429: 'Слишком много попыток. Подожди минуту.',
  503: 'Веб-доступ выключен на сервере.',
};

/**
 * @param {{ base?: string, timeoutMs?: number, onUnauthorized?: () => void, onNetwork?: (online: boolean) => void }} opts
 */
export function createApi(opts = {}) {
  const base = opts.base ?? '';
  const timeoutMs = opts.timeoutMs ?? 20000;

  // `raw` is a content type: the body (a Blob) is sent as is instead of JSON. `timeout` overrides the default.
  async function request(method, path, body, { anonymous = false, raw = null, timeout = timeoutMs } = {}) {
    const headers = { Accept: 'application/json' };
    if (method !== 'GET') headers['X-TimeShift'] = '1';
    if (body !== undefined) headers['Content-Type'] = raw ?? 'application/json';
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), timeout);
    let res;
    try {
      res = await fetch(base + path, {
        method, headers, credentials: 'same-origin', cache: 'no-store', signal: ctrl.signal,
        body: body === undefined ? undefined : raw ? body : JSON.stringify(body),
      });
    } catch (e) {
      clearTimeout(timer);
      const timeout = e?.name === 'AbortError';
      opts.onNetwork?.(false);
      throw new ApiError(0, timeout ? 'timeout' : 'offline', timeout ? 'Сервер долго не отвечает. Попробуй ещё раз.' : FALLBACK_MESSAGES[0]);
    }
    clearTimeout(timer);
    opts.onNetwork?.(true);
    if (res.status === 204) return null;
    let data = null;
    const text = await res.text();
    if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    if (!res.ok) {
      const err = new ApiError(res.status, data?.error ?? 'http', data?.message || FALLBACK_MESSAGES[res.status] || 'Что-то пошло не так.', data?.field ?? null);
      if (res.status === 401 && !anonymous) opts.onUnauthorized?.();
      throw err;
    }
    return data;
  }

  const q = (view) => (view ? `?view=${encodeURIComponent(view)}` : '');
  const get = (p, o) => request('GET', p, undefined, o);
  const post = (p, b, o) => request('POST', p, b ?? undefined, o);

  return {
    demo: false,
    // meta and auth
    meta: () => get('/api/meta', { anonymous: true }),
    login: (key) => post('/api/auth/login', { key }, { anonymous: true }),
    logout: () => post('/api/auth/logout', undefined, { anonymous: true }),
    // profile and template → Me
    me: () => get('/api/me'),
    saveSettings: (patch) => request('PATCH', '/api/settings', patch),
    setSleep: (sleep) => request('PUT', '/api/schedule/sleep', sleep),
    addBlock: (block) => post('/api/schedule/blocks', block),
    updateBlock: (id, block) => request('PUT', `/api/schedule/blocks/${encodeURIComponent(id)}`, block),
    deleteBlock: (id) => request('DELETE', `/api/schedule/blocks/${encodeURIComponent(id)}`),
    completeOnboarding: () => post('/api/onboarding/complete'),
    // reading the timeline
    day: (date) => get(`/api/day/${encodeURIComponent(date)}`),
    summary: (from, to) => get(`/api/summary?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`),
    // changing the timeline → { day }
    start: (body, view) => post(`/api/now/start${q(view)}`, body),
    stop: (body, view) => post(`/api/now/stop${q(view)}`, body ?? {}),
    continue: (view) => post(`/api/now/continue${q(view)}`),
    addEntry: (body, view) => post(`/api/entries${q(view)}`, body),
    updateEntry: (id, patch, view) => request('PATCH', `/api/entries/${encodeURIComponent(id)}${q(view)}`, patch),
    deleteEntry: (id, view) => request('DELETE', `/api/entries/${encodeURIComponent(id)}${q(view)}`),
    confirm: (body, view) => post(`/api/confirm${q(view)}`, body),
    undo: (view) => post(`/api/undo${q(view)}`),
    quiet: (minutes, view) => post(`/api/quiet${q(view)}`, { minutes }),
    // activities
    activities: () => get('/api/activities'),
    updateActivity: (id, patch) => request('PATCH', `/api/activities/${encodeURIComponent(id)}`, patch),
    // free text → { replies, day }
    message: (body, view) => post(`/api/messages${q(view)}`, body),
    // a recording (WAV 16 kHz mono, see voice.js) → the same { replies, day }; recognition is slow, so a long timeout
    voice: (wav, view) => post(`/api/messages${q(view)}`, wav, { raw: 'audio/wav', timeout: VOICE_TIMEOUT_MS }),
    // push
    pushInfo: () => get('/api/push'),
    subscribePush: (sub) => post('/api/push/subscriptions', sub),
    unsubscribePush: (endpoint) => request('DELETE', '/api/push/subscriptions', { endpoint }),
    testPush: () => post('/api/push/test'),
  };
}
