// The app: state (immutable snapshots), actions, refresh and clock. Designs render from `state` and call `actions`.
import { createApi, ApiError } from './api.js';
import { createDemoApi, demoClockOffset } from './demo.js';
import * as T from './time.js';
import { advanceDay, patchDay, assumedPieces } from './timeline.js';
import { showToast } from './toast.js';
import * as pwa from './pwa.js';
import { openSheetCount } from './sheet.js';
import { isTyping } from './dom.js';
import { findDesign, storeDesign, designUrl, setReopen } from './designs.js';

const SNAPSHOT_KEY = 'ts.snapshot.v1';
const THEME_KEY = 'ts.theme';
const DESIGN_KEY = 'ts.design';
const TICK_MS = 15000;
const REFRESH_MS = 60000;
const CACHE_SIZE = 9;

const ls = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* quota / private */ } },
  del(k) { try { localStorage.removeItem(k); } catch { /* ignore */ } },
};
const ss = {
  get(k) { try { return sessionStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { sessionStorage.setItem(k, v); } catch { /* ignore */ } },
  del(k) { try { sessionStorage.removeItem(k); } catch { /* ignore */ } },
};
const freeze = (o) => Object.freeze(o);

/** Apply a theme preference ('auto' | 'light' | 'dark') to <html>: data-theme is always the resolved value. */
export function applyTheme(pref) {
  const mq = matchMedia('(prefers-color-scheme: dark)');
  const resolved = pref === 'light' || pref === 'dark' ? pref : mq.matches ? 'dark' : 'light';
  const root = document.documentElement;
  root.dataset.theme = resolved;
  root.dataset.themePref = pref === 'light' || pref === 'dark' ? pref : 'auto';
  root.style.colorScheme = resolved;
  return resolved;
}

/**
 * @param {{ design?: string, api?: object }} [opts]
 * @returns the app: { state, subscribe(fn), start(), actions, nowMs(), api }
 */
export function createApp(opts = {}) {
  const url = new URL(location.href);
  const params = url.searchParams;
  // The demo flag is remembered for the tab — except in an embedded frame, which shares sessionStorage with it.
  const embedded = params.get('embed') === '1' || window.top !== window;
  if (!embedded && params.get('demo') === '1') ss.set('ts.demo', '1');
  if (params.get('demo') === '0') { ss.del('ts.demo'); ss.del('ts.demo.at'); }
  const demo = !opts.api && (params.get('demo') === '1' || (params.get('demo') !== '0' && ss.get('ts.demo') === '1'));
  // Demo clock: ?demo=1&at=HH:mm — "now" is that local time today, ticking on. Remembered with the demo flag.
  if (!embedded && params.get('demo') === '1') { if (params.has('at')) ss.set('ts.demo.at', params.get('at')); else ss.del('ts.demo.at'); }
  const demoAt = demo ? (params.get('at') ?? (embedded ? null : ss.get('ts.demo.at'))) : null;
  const demoOffset = demoAt ? demoClockOffset(demoAt) ?? 0 : 0;

  let onUnauthorized = () => {};
  let onNetwork = () => {};
  const api = opts.api ?? (demo
    ? createDemoApi({ fresh: params.get('fresh') === '1', login: params.get('login') === '1', speech: params.get('speech') !== '0', clockOffsetMs: demoOffset, onUnauthorized: () => onUnauthorized() })
    : createApi({ onUnauthorized: () => onUnauthorized(), onNetwork: (on) => onNetwork(on) }));

  const themePref = ls.get(THEME_KEY) ?? 'auto';
  let state = freeze({
    phase: 'loading',
    bootError: null,
    loginError: null,
    demo,
    demoAt: demoAt && demoClockOffset(demoAt) !== null ? demoAt : null,
    embed: params.get('embed') === '1',
    design: opts.design ?? null,
    online: navigator.onLine !== false,
    meta: null,
    me: null,
    today: null,
    viewDate: null,
    day: null,
    dayLoading: false,
    dayError: null,
    nowMin: null,
    activities: [],
    busy: null,
    thread: [],
    threadBusy: false,
    summary: null,
    ping: { requested: params.get('from') === 'ping', pending: false },
    push: { supported: false, ios: pwa.isIos, standalone: pwa.isStandalone(), permission: 'default', subscribed: false, needsInstall: false },
    install: pwa.installInfo(),
    theme: themePref,
    themeResolved: applyTheme(themePref),
    reloadPending: false,
  });

  // Strip one-shot params (?from=ping) so a reload does not re-trigger them.
  if (params.has('from')) {
    params.delete('from');
    history.replaceState(history.state, '', url.pathname + (params.toString() ? `?${params}` : '') + url.hash);
  }

  // ---------- state ----------
  const subs = new Set();
  let prevForSubs = state;
  let scheduled = false;
  function set(patch) {
    state = freeze({ ...state, ...patch });
    if (!scheduled) {
      scheduled = true;
      queueMicrotask(() => {
        scheduled = false;
        const prev = prevForSubs;
        prevForSubs = state;
        for (const fn of [...subs]) { try { fn(state, prev); } catch (e) { console.error(e); } }
      });
    }
  }

  // ---------- clock ----------
  let clockOffset = demoOffset;
  const nowMs = () => Date.now() + clockOffset;
  const tz = () => state.me?.timeZone ?? T.deviceTimeZone();
  const computeToday = () => T.todayIn(tz(), nowMs());

  // ---------- day cache ----------
  const cache = new Map(); // date → { day, at }
  let mutationSeq = 0;
  function nowMinFor(raw) {
    if (!raw || raw.nowMin == null) return null;
    const m = Math.floor((nowMs() - Date.parse(raw.startUtc)) / 60000);
    return Math.max(0, Math.min(raw.dayMinutes, m));
  }
  function putDay(day) {
    cache.set(day.date, { day, at: Date.now() });
    if (cache.size > CACHE_SIZE && state.viewDate) {
      const far = [...cache.keys()].sort((a, b) => Math.abs(T.diffDays(state.viewDate, b)) - Math.abs(T.diffDays(state.viewDate, a)))[0];
      if (far !== state.viewDate) cache.delete(far);
    }
    if (day.date === state.viewDate) publishDay();
    if (day.date === state.today) saveSnapshot();
  }
  function publishDay() {
    const raw = cache.get(state.viewDate)?.day ?? null;
    const nowMin = state.viewDate === state.today ? nowMinFor(raw) : null;
    const day = raw && nowMin != null ? advanceDay(raw, nowMin) : raw;
    set({
      day, nowMin: day?.nowMin ?? null,
      ping: { ...state.ping, pending: !!day?.attention?.pingPending },
    });
  }
  function invalidateExcept(date) { for (const k of [...cache.keys()]) if (k !== date) cache.delete(k); }

  async function fetchDay(date, { quiet = false } = {}) {
    const seq = mutationSeq;
    const t0 = Date.now();
    if (!quiet && date === state.viewDate && !cache.has(date)) set({ dayLoading: true, dayError: null });
    try {
      const day = await api.day(date);
      if (!demo && day.nowUtc) clockOffset = Date.parse(day.nowUtc) - (t0 + Date.now()) / 2;
      if (seq !== mutationSeq) return null; // a mutation answered meanwhile: its day is newer
      putDay(day);
      if (date === state.viewDate) set({ dayLoading: false, dayError: null });
      return day;
    } catch (e) {
      if (date === state.viewDate) set({ dayLoading: false, dayError: cache.has(date) ? null : e });
      if (!quiet && e.status !== 401 && e.code !== 'offline') toastError(e);
      return null;
    }
  }
  let prefetchTimer = 0;
  function schedulePrefetch() {
    clearTimeout(prefetchTimer);
    prefetchTimer = setTimeout(() => {
      const v = state.viewDate;
      if (!v || state.phase !== 'ready') return;
      for (const d of [T.addDays(v, -1), T.addDays(v, 1)]) if (!cache.has(d)) fetchDay(d, { quiet: true });
    }, 250);
  }

  // ---------- snapshot (instant start) ----------
  let snapTimer = 0;
  function saveSnapshot() {
    if (demo) return;
    clearTimeout(snapTimer);
    snapTimer = setTimeout(() => {
      const today = cache.get(state.today)?.day ?? null;
      ls.set(SNAPSHOT_KEY, JSON.stringify({ me: state.me, activities: state.activities, meta: state.meta, day: today, savedAt: Date.now() }));
    }, 300);
  }
  function restoreSnapshot() {
    if (demo) return false;
    let snap = null;
    try { snap = JSON.parse(ls.get(SNAPSHOT_KEY) ?? 'null'); } catch { snap = null; }
    if (!snap?.me?.onboarded) return false;
    set({ me: snap.me, activities: snap.activities ?? [], meta: snap.meta ?? null });
    const today = computeToday();
    set({ today, viewDate: today, phase: 'ready' });
    if (snap.day?.date === today) putDay(snap.day);
    else set({ dayLoading: true });
    return true;
  }

  // ---------- errors and toasts ----------
  function toastError(e) {
    if (e?.status === 401) return;
    showToast({ message: e?.message || 'Что-то пошло не так.', kind: 'error' });
  }
  onUnauthorized = () => {
    ls.del(SNAPSHOT_KEY);
    cache.clear();
    set({ phase: 'login', me: null, day: null, loginError: null });
  };
  onNetwork = (on) => { if (state.online !== on) set({ online: on }); };

  // ---------- boot ----------
  let booting = null;
  function boot() {
    if (!booting) booting = doBoot().finally(() => { booting = null; });
    return booting;
  }
  async function doBoot() {
    let meta;
    try {
      meta = await api.meta();
    } catch (e) {
      if (state.phase !== 'ready') set({ phase: 'loading', bootError: e });
      return;
    }
    set({ meta, bootError: null });
    if (!meta.webEnabled) { set({ phase: 'login', loginError: 'Веб-доступ не настроен на сервере.' }); return; }
    if (!meta.authenticated) { onUnauthorized(); return; }
    await loadUser();
    pwa.checkAssets(meta.assetsVersion).then((due) => { if (due) set({ reloadPending: true }); }).catch(() => {});
  }
  async function loadUser() {
    let me;
    let activities;
    try {
      [me, activities] = await Promise.all([api.me(), api.activities()]);
    } catch (e) {
      if (state.phase !== 'ready') set({ phase: e.status === 401 ? 'login' : 'loading', bootError: e.status === 401 ? null : e });
      return;
    }
    lastServerMe = me;
    set({ me, activities });
    if (!me.onboarded) { set({ phase: 'onboarding' }); return; }
    const today = computeToday();
    const keepView = state.phase === 'ready' && state.viewDate;
    set({ today, viewDate: keepView ? state.viewDate : today, phase: 'ready', bootError: null });
    await fetchDay(state.viewDate, { quiet: cache.has(state.viewDate) });
    schedulePrefetch();
    saveSnapshot();
  }

  // ---------- refresh ----------
  let refreshing = null;
  let lastRefresh = 0;
  let lastMe = 0;
  async function refresh({ full = false } = {}) {
    if (state.phase === 'loading' || state.phase === 'login') { if (state.phase === 'loading') return boot(); return; }
    if (refreshing) return refreshing;
    lastRefresh = Date.now();
    refreshing = (async () => {
      if (state.phase === 'onboarding') return;
      const jobs = [fetchDay(state.viewDate, { quiet: true })];
      if (full || Date.now() - lastMe > 5 * 60000) {
        lastMe = Date.now();
        jobs.push(api.me().then((me) => { lastServerMe = me; set({ me }); }).catch(() => {}));
        jobs.push(api.activities().then((a) => set({ activities: a })).catch(() => {}));
      }
      await Promise.all(jobs);
    })().finally(() => { refreshing = null; });
    return refreshing;
  }

  function tick() {
    if (state.phase === 'ready') {
      const today = computeToday();
      if (state.today && today !== state.today) {
        const followed = state.viewDate === state.today;
        set({ today });
        if (followed) actions.goToDay(today);
        else refresh();
      } else publishDay();
    }
    maybeReload();
  }

  let lastInteraction = 0;
  function maybeReload() {
    if (!state.reloadPending) return;
    const safe = document.hidden || (openSheetCount() === 0 && !isTyping() && !state.busy && Date.now() - lastInteraction > 2500);
    if (safe) location.reload();
  }

  // ---------- mutations ----------
  function viewOrToday() { return state.viewDate ?? state.today ?? computeToday(); }
  function currentRaw(view) { const r = cache.get(view)?.day ?? null; return r && view === state.today ? advanceDay(r, nowMinFor(r)) : r; }

  /**
   * Timeline mutation: optimistic patch, server call, undo toast, rollback + error toast on failure.
   * @returns {Promise<{ ok: boolean, result?, error? }>}
   */
  async function mutate(name, { call, optimistic, toast, undoable = true, inlineErrors = false }) {
    if (state.busy) return { ok: false, error: new ApiError(0, 'busy', 'Подожди, предыдущее ещё сохраняется.') };
    const view = viewOrToday();
    const before = cache.get(view)?.day ?? null;
    set({ busy: name });
    if (optimistic && before) {
      const patched = patchDay(currentRaw(view), optimistic);
      cache.set(view, { day: patched, at: Date.now() });
      if (view === state.viewDate) publishDay();
    }
    mutationSeq++;
    try {
      const result = await call(view);
      mutationSeq++;
      invalidateExcept(view);
      if (result?.day) putDay(result.day);
      if (toast) {
        const message = typeof toast === 'function' ? toast(result) : toast;
        if (message) showToast({ message, action: undoable ? { label: 'Отменить', onClick: () => actions.undo() } : null });
      }
      schedulePrefetch();
      return { ok: true, result };
    } catch (error) {
      if (before) { cache.set(view, { day: before, at: Date.now() }); if (view === state.viewDate) publishDay(); }
      if (!(inlineErrors && error.status === 422)) toastError(error);
      return { ok: false, error };
    } finally {
      set({ busy: null });
    }
  }

  // Me mutations run one after another; failure rolls back to the last server state.
  let lastServerMe = null;
  let meQueue = Promise.resolve();
  function meMutation(call, optimisticMe, { refreshDay = false, inlineErrors = false } = {}) {
    if (optimisticMe && state.me) set({ me: optimisticMe(state.me) });
    const run = async () => {
      try {
        const me = await call();
        lastServerMe = me;
        set({ me });
        saveSnapshot();
        if (refreshDay) { mutationSeq++; cache.clear(); await fetchDay(state.viewDate, { quiet: true }); schedulePrefetch(); }
        return { ok: true, result: me };
      } catch (error) {
        if (lastServerMe) set({ me: lastServerMe });
        if (!(inlineErrors && error.status === 422)) toastError(error);
        return { ok: false, error };
      }
    };
    const p = meQueue.then(run);
    meQueue = p.catch(() => {});
    return p;
  }

  const activityById = (id) => state.activities.find((a) => a.id === id);
  const minOf = (local) => (local ? T.localToMin(local, viewOrToday()) : null);
  const bump = (activityId, title) => {
    // Move the used activity to the front of the recent list right away.
    const list = state.activities.slice();
    const i = list.findIndex((a) => (activityId ? a.id === activityId : a.name.toLowerCase() === (title ?? '').toLowerCase()));
    if (i > 0) { const [a] = list.splice(i, 1); list.unshift({ ...a, lastUsedUtc: new Date(nowMs()).toISOString() }); set({ activities: list }); }
  };
  const refreshActivities = () => api.activities().then((a) => set({ activities: a })).catch(() => {});

  function thread(update) { set({ thread: update(state.thread) }); }
  let msgId = 0;

  /** The user's message (typed or spoken) goes into the thread, then the server's replies. */
  async function say(message, call) {
    if (state.threadBusy) return { ok: false };
    const id = `u${++msgId}`;
    thread((th) => [...th, { id, from: 'user', ...message, pending: true }]);
    set({ threadBusy: true });
    try {
      const res = await call(viewOrToday());
      mutationSeq++;
      thread((th) => [...th.map((m) => (m.id === id ? { ...m, pending: false } : m)),
        ...res.replies.map((r) => ({ id: `b${++msgId}`, from: 'bot', text: r.text, buttons: r.buttons ?? [], hasImage: !!r.hasImage }))]);
      if (res.day) { invalidateExcept(res.day.date); putDay(res.day); }
      refreshActivities();
      return { ok: true, result: res };
    } catch (error) {
      thread((th) => th.map((m) => (m.id === id ? { ...m, pending: false, error: error.message } : m)));
      toastError(error);
      return { ok: false, error };
    } finally { set({ threadBusy: false }); }
  }

  const actions = {
    // ----- phases -----
    retry: () => boot(),
    async login(key) {
      set({ busy: 'login', loginError: null });
      try {
        await api.login(key);
        set({ busy: null });
        await boot();
        return { ok: true };
      } catch (error) {
        set({ busy: null, loginError: error.status === 401 ? 'Неверный ключ.' : error.message });
        return { ok: false, error };
      }
    },
    async logout() {
      try { await api.logout(); } catch { /* ignore */ }
      onUnauthorized();
    },
    async completeOnboarding() {
      set({ busy: 'onboarding' });
      try {
        const me = await api.completeOnboarding();
        lastServerMe = me;
        set({ me, busy: null });
        await loadUser();
        return { ok: true };
      } catch (error) {
        set({ busy: null });
        toastError(error);
        return { ok: false, error };
      }
    },

    // ----- navigation -----
    goToDay(date) {
      if (!T.isDate(date) || date === state.viewDate) return;
      const cached = cache.has(date);
      set({ viewDate: date, dayLoading: !cached, dayError: null, summary: null });
      publishDay();
      fetchDay(date, { quiet: cached });
      schedulePrefetch();
    },
    prevDay() { actions.goToDay(T.addDays(state.viewDate, -1)); },
    nextDay() { actions.goToDay(T.addDays(state.viewDate, 1)); },
    goToday() { actions.goToDay(computeToday()); },
    refresh: (o) => refresh(o),

    // ----- now -----
    start({ activityId, title, at, category } = {}) {
      const a = activityId ? activityById(activityId) : null;
      const name = a?.name ?? title?.trim();
      if (!name) return Promise.resolve({ ok: false });
      bump(activityId, name);
      const body = activityId ? { activityId } : { title: name };
      if (at) body.at = at;
      return mutate('start', {
        call: (v) => api.start(body, v),
        optimistic: { type: 'start', title: name, category: a?.category ?? category ?? null, activityId: activityId ?? null, atMin: minOf(at) ?? undefined },
        toast: `Начато: ${name}`,
      }).then((r) => { if (r.ok && !activityId) refreshActivities(); return r; });
    },
    stop({ at } = {}) {
      const title = state.day?.current?.title;
      return mutate('stop', {
        call: (v) => api.stop(at ? { at } : {}, v),
        optimistic: { type: 'stop', atMin: minOf(at) ?? undefined },
        toast: title ? `Закончено: ${title}` : 'Закончено',
      });
    },
    continueCurrent() {
      const title = state.day?.current?.title;
      return mutate('continue', { call: (v) => api.continue(v), optimistic: { type: 'continue' }, toast: title ? `Продолжаю: ${title}` : 'Отмечено' });
    },

    // ----- entries -----
    addEntry(entry, o = {}) {
      const a = entry.activityId ? activityById(entry.activityId) : null;
      const title = a?.name ?? entry.title;
      const body = { start: entry.start, end: entry.end, ...(entry.activityId ? { activityId: entry.activityId } : { title }) };
      if (entry.category) body.category = entry.category;
      if (entry.parallel) body.parallel = true;
      bump(entry.activityId, title);
      return mutate('addEntry', {
        call: (v) => api.addEntry(body, v),
        optimistic: { type: 'overlay', startMin: minOf(entry.start), endMin: minOf(entry.end), title, category: entry.category ?? a?.category ?? null, activityId: entry.activityId, parallel: entry.parallel },
        toast: `Записано: ${title}, ${T.fmtRange(minOf(entry.start), minOf(entry.end))}`,
        inlineErrors: o.inlineErrors,
      }).then((r) => { if (r.ok) refreshActivities(); return r; });
    },
    updateEntry(segmentId, patch, o = {}) {
      // o.range = { start, end } (local) of the edited segment after the change, for the optimistic picture.
      const range = o.range ?? (patch.start && patch.end ? { start: patch.start, end: patch.end } : null);
      const optimistic = range?.start && range?.end
        ? { type: 'overlay', startMin: minOf(range.start), endMin: minOf(range.end), title: patch.title ?? o.title ?? '', category: patch.category ?? o.category ?? null, parallel: o.parallel, segmentId }
        : null;
      return mutate('updateEntry', { call: (v) => api.updateEntry(segmentId, patch, v), optimistic, toast: 'Изменено', inlineErrors: o.inlineErrors })
        .then((r) => { if (r.ok && patch.title) refreshActivities(); return r; });
    },
    deleteEntry(segmentId) {
      return mutate('deleteEntry', { call: (v) => api.deleteEntry(segmentId, v), optimistic: { type: 'free', segmentId }, toast: 'Удалено' });
    },
    confirm({ start, end, includeQuestionable = false }) {
      return mutate('confirm', {
        call: (v) => api.confirm({ start, end, includeQuestionable }, v),
        optimistic: { type: 'confirm', startMin: minOf(start), endMin: minOf(end), includeQuestionable },
        toast: 'Подтверждено',
      });
    },
    /** Confirm every assumed past piece of the viewed day. */
    confirmDay() {
      const day = state.day;
      if (!day || !assumedPieces(day).length) return Promise.resolve({ ok: false });
      const end = day.nowMin ?? day.dayMinutes;
      return actions.confirm({ start: T.minToLocal(day.date, 0), end: T.minToLocal(day.date, end) });
    },
    undo() {
      return mutate('undo', {
        call: (v) => api.undo(v),
        toast: (r) => (r?.result === 'undone' ? 'Отменено' : r?.result === 'stale' ? 'Уже нельзя отменить' : 'Нечего отменять'),
        undoable: false,
      }).then((r) => { if (r.ok) { refreshActivities(); api.me().then((me) => { lastServerMe = me; set({ me }); }).catch(() => {}); } return r; });
    },
    quiet(minutes) {
      return mutate('quiet', {
        call: (v) => api.quiet(minutes, v),
        toast: minutes ? `Тишина до ${T.fmtClock(T.nowMinIn(tz(), nowMs() + minutes * 60000))}` : 'Напоминания снова включены',
      }).then((r) => {
        if (r.ok) {
          const until = minutes ? new Date(nowMs() + minutes * 60000).toISOString() : null;
          set({ me: { ...state.me, quietUntilUtc: until } });
        }
        return r;
      });
    },

    // ----- free text and voice -----
    sendMessage(text) {
      const t = (text ?? '').trim();
      if (!t) return Promise.resolve({ ok: false });
      return say({ text: t }, (view) => api.message({ text: t }, view));
    },
    /** A recording from core/voice.js; the first reply is what the server heard. */
    sendVoice(wav, seconds) {
      if (!wav) return Promise.resolve({ ok: false });
      const sec = Math.max(1, Math.round(seconds ?? 0));
      return say({ text: `🎙 Голосовое · ${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`, voice: true }, (view) => api.voice(wav, view));
    },
    async pressButton(data, messageId) {      if (state.threadBusy) return { ok: false };
      thread((th) => th.map((m) => (m.id === messageId ? { ...m, pressed: data } : m)));
      set({ threadBusy: true });
      try {
        const res = await api.message({ button: data }, viewOrToday());
        mutationSeq++;
        thread((th) => {
          let out = th.slice();
          for (const r of res.replies) {
            const msg = { id: `b${++msgId}`, from: 'bot', text: r.text, buttons: r.buttons ?? [], hasImage: !!r.hasImage };
            const i = r.replacesSource ? out.findIndex((m) => m.id === messageId) : -1;
            if (i >= 0) out[i] = { ...msg, id: messageId };
            else out = [...out, msg];
          }
          return out;
        });
        if (res.day) { invalidateExcept(res.day.date); putDay(res.day); }
        return { ok: true, result: res };
      } catch (error) {
        thread((th) => th.map((m) => (m.id === messageId ? { ...m, pressed: null } : m)));
        toastError(error);
        return { ok: false, error };
      } finally { set({ threadBusy: false }); }
    },
    clearThread() { set({ thread: [] }); },

    // ----- settings and template -----
    saveSettings(patch, o = {}) {
      const { timeZone, displayName, ...settings } = patch;
      const affectsDay = 'timeZone' in patch || 'normFactor' in patch || 'maxFactor' in patch;
      return meMutation(() => api.saveSettings(patch), (me) => ({
        ...me, ...(timeZone ? { timeZone } : {}), ...(displayName != null ? { displayName } : {}), settings: { ...me.settings, ...settings },
      }), { refreshDay: affectsDay && state.phase === 'ready', inlineErrors: o.inlineErrors }).then((r) => {
        if (r.ok && timeZone) set({ today: computeToday() });
        return r;
      });
    },
    setSleep(sleep) {
      return meMutation(() => api.setSleep(sleep), (me) => ({ ...me, sleep }), { refreshDay: state.phase === 'ready' });
    },
    addBlock(block) {
      return meMutation(() => api.addBlock(block), (me) => ({ ...me, blocks: [...me.blocks, { ...block, id: `local:${Date.now()}` }] }), { refreshDay: state.phase === 'ready' });
    },
    updateBlock(id, block) {
      return meMutation(() => api.updateBlock(id, block), (me) => ({ ...me, blocks: me.blocks.map((b) => (b.id === id ? { ...b, ...block } : b)) }), { refreshDay: state.phase === 'ready' });
    },
    deleteBlock(id) {
      return meMutation(() => api.deleteBlock(id), (me) => ({ ...me, blocks: me.blocks.filter((b) => b.id !== id) }), { refreshDay: state.phase === 'ready' });
    },
    async updateActivity(id, patch) {
      const prev = state.activities;
      set({ activities: prev.map((a) => (a.id === id ? { ...a, ...patch } : a)) });
      try {
        const a = await api.updateActivity(id, patch);
        set({ activities: state.activities.map((x) => (x.id === id ? a : x)) });
        if ('name' in patch || 'typicalMin' in patch) { mutationSeq++; cache.clear(); fetchDay(state.viewDate, { quiet: true }); }
        return { ok: true, result: a };
      } catch (error) {
        set({ activities: prev });
        toastError(error);
        return { ok: false, error };
      }
    },

    // ----- summary -----
    async loadSummary({ from, to } = {}) {
      const end = to ?? state.viewDate ?? computeToday();
      const start = from ?? T.addDays(end, -6);
      try {
        const data = await api.summary(start, end);
        set({ summary: { from: start, to: end, data } });
        return data;
      } catch (error) {
        toastError(error);
        return null;
      }
    },

    // ----- push and install -----
    enablePush() {
      // Must stay synchronous up to the permission prompt (user gesture).
      const p = pwa.enablePush(api, state.meta?.push?.publicKey ?? null);
      set({ busy: 'push' });
      return p.then(async () => {
        set({ push: await pwa.pushState(), busy: null });
        showToast({ message: 'Уведомления включены' });
        return { ok: true };
      }, async (error) => {
        set({ push: await pwa.pushState(), busy: null });
        toastError(error);
        return { ok: false, error };
      });
    },
    async disablePush() {
      await pwa.disablePush(api);
      set({ push: await pwa.pushState() });
      return { ok: true };
    },
    async testPush() {
      try { await api.testPush(); showToast({ message: 'Проверочное уведомление отправлено' }); return { ok: true }; }
      catch (error) { toastError(error); return { ok: false, error }; }
    },
    async promptInstall() {
      const r = await pwa.promptInstall();
      set({ install: pwa.installInfo() });
      return r;
    },

    // ----- appearance and design -----
    setTheme(pref) {
      const p = ['light', 'dark'].includes(pref) ? pref : 'auto';
      ls.set(THEME_KEY, p);
      set({ theme: p, themeResolved: applyTheme(p) });
    },
    chooseDesign(x) {
      ls.set(DESIGN_KEY, x);
      location.href = `/${x}/${demo ? '?demo=1' : ''}`;
    },
    /**
     * Switch to another design in place: store it, open /<id>/ with the demo flags, and reopen the settings sheet
     * on `page` there. Does nothing in an embedded frame or for an unknown id. Returns true when it navigates.
     */
    switchDesign(id, { page = 'other' } = {}) {
      if (state.embed || !findDesign(id)) return false;
      storeDesign(id);
      if (id === state.design) return false;
      setReopen({ flow: 'settings', page });
      const q = new URLSearchParams(location.search);
      if (demo && !q.has('demo')) q.set('demo', '1');
      if (state.demoAt && !q.has('at')) q.set('at', state.demoAt);
      location.href = designUrl(id, `?${q}`);
      return true;
    },
    clearDesign() {
      ls.del(DESIGN_KEY);
      location.href = `/?choose${demo ? '&demo=1' : ''}`;
    },

    // ----- ping -----
    clearPing() { set({ ping: { ...state.ping, requested: false } }); },
    requestPing() { set({ ping: { ...state.ping, requested: true } }); },
  };

  // ---------- start ----------
  let started = false;
  const timers = [];
  function start() {
    if (started) return app;
    started = true;
    restoreSnapshot();
    boot();
    const mq = matchMedia('(prefers-color-scheme: dark)');
    mq.addEventListener?.('change', () => { if (state.theme === 'auto') set({ themeResolved: applyTheme('auto') }); });
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) { maybeReload(); return; }
      tick();
      refresh({ full: true });
    });
    window.addEventListener('focus', () => { if (Date.now() - lastRefresh > 5000) refresh({ full: true }); });
    window.addEventListener('online', () => { set({ online: true }); refresh({ full: true }); });
    window.addEventListener('offline', () => set({ online: false }));
    window.addEventListener('pageshow', (e) => { if (e.persisted) { tick(); refresh({ full: true }); } });
    for (const ev of ['pointerdown', 'keydown']) document.addEventListener(ev, () => { lastInteraction = Date.now(); }, { passive: true, capture: true });
    timers.push(setInterval(tick, TICK_MS));
    timers.push(setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS));
    pwa.registerServiceWorker().then(() => pwa.pushState()).then((push) => set({ push }));
    pwa.onPwa((type) => {
      if (type === 'ping') { actions.requestPing(); refresh(); }
      if (type === 'install') set({ install: pwa.installInfo() });
      if (type === 'changed') refresh({ full: true });
    });
    return app;
  }

  const app = {
    get state() { return state; },
    /** fn(state, prev) is called once right away and after every change (batched per microtask). Returns unsubscribe. */
    subscribe(fn, { immediate = true } = {}) {
      subs.add(fn);
      if (immediate) fn(state, state);
      return () => subs.delete(fn);
    },
    start,
    actions,
    nowMs,
    /** The cached (or prefetched) day for a date, projected to "now" for today; null when not cached. Never fetches. */
    peekDay(date) {
      const raw = cache.get(date)?.day ?? null;
      if (!raw) return null;
      return date === state.today ? advanceDay(raw, nowMinFor(raw)) : raw;
    },
    api,
  };
  return app;
}
