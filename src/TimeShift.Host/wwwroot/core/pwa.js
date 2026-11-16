// PWA plumbing: service worker, install hint, Web Push, asset refresh.

const ua = navigator.userAgent;
export const isIos = /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
export const isStandalone = () => matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
const swSupported = 'serviceWorker' in navigator && (location.protocol === 'https:' || ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname));

let deferredPrompt = null;
const listeners = new Set();
const emit = (type, data) => { for (const fn of listeners) fn(type, data); };
/** Subscribe to PWA events: 'ping' (notification tapped), 'install' (install state changed), 'refreshed'. */
export const onPwa = (fn) => { listeners.add(fn); return () => listeners.delete(fn); };

window.addEventListener('beforeinstallprompt', (e) => { e.preventDefault(); deferredPrompt = e; emit('install', installInfo()); });
window.addEventListener('appinstalled', () => { deferredPrompt = null; emit('install', installInfo()); });

let registration = null;
/** Register /sw.js (scope '/'). Safe to call more than once. */
export async function registerServiceWorker() {
  if (!swSupported) return null;
  if (registration) return registration;
  try {
    registration = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
  } catch { return null; }
  navigator.serviceWorker.addEventListener('message', (e) => {
    const m = e.data ?? {};
    if (m.type === 'ping') emit('ping', m);
    else if (m.type === 'refreshed' || m.type === 'changed') emit(m.type, m);
  });
  return registration;
}

/**
 * Install hint for the UI.
 * kind: 'installed' (standalone), 'ios' (Safari: Поделиться → На экран «Домой»), 'prompt' (Android: call promptInstall),
 *       'none' (nothing to offer).
 */
export function installInfo() {
  if (isStandalone()) return { kind: 'installed', standalone: true, ios: isIos, text: 'Приложение установлено.' };
  if (isIos) return { kind: 'ios', standalone: false, ios: true, text: 'Нажми «Поделиться», затем «На экран „Домой“». Уведомления на iPhone работают только из установленного приложения.' };
  if (deferredPrompt) return { kind: 'prompt', standalone: false, ios: false, text: 'Можно установить TimeShift как приложение.' };
  return { kind: 'none', standalone: false, ios: false, text: 'Установить можно из меню браузера: «Установить приложение» или «Добавить на главный экран».' };
}

/** Android / desktop Chrome install prompt. Must run inside a user gesture. Resolves to 'accepted' | 'dismissed' | 'unavailable'. */
export async function promptInstall() {
  if (!deferredPrompt) return 'unavailable';
  const p = deferredPrompt;
  deferredPrompt = null;
  p.prompt();
  const choice = await p.userChoice.catch(() => ({ outcome: 'dismissed' }));
  emit('install', installInfo());
  return choice.outcome;
}

// ---------- push ----------

/** { supported, ios, standalone, permission, subscribed, needsInstall } */
export async function pushState() {
  const standalone = isStandalone();
  const supported = swSupported && 'PushManager' in window && 'Notification' in window;
  const base = { supported, ios: isIos, standalone, permission: 'Notification' in window ? Notification.permission : 'unsupported', subscribed: false, needsInstall: isIos && !standalone };
  if (!supported) return base;
  try {
    const reg = registration ?? (await navigator.serviceWorker.getRegistration('/'));
    const sub = await reg?.pushManager.getSubscription();
    return { ...base, subscribed: !!sub };
  } catch { return base; }
}

function keyBytes(base64) {
  const pad = '='.repeat((4 - (base64.length % 4)) % 4);
  const raw = atob((base64 + pad).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

/**
 * Turn notifications on for this device. MUST be called synchronously from a user gesture (tap):
 * the permission prompt is requested first, before any await.
 * @param {object} api      api.js / demo.js client
 * @param {string|null} publicKey  VAPID key from meta.push (null in demo: only the permission is asked)
 */
export async function enablePush(api, publicKey) {
  if (!('Notification' in window)) throw new Error('Уведомления не поддерживаются этим браузером.');
  if (isIos && !isStandalone()) throw new Error('На iPhone сначала добавь TimeShift на экран «Домой», затем включи уведомления из приложения.');
  const permission = await Notification.requestPermission();
  if (permission !== 'granted') throw new Error(permission === 'denied' ? 'Уведомления запрещены в настройках браузера.' : 'Разрешение не выдано.');
  const reg = registration ?? (await registerServiceWorker()) ?? (await navigator.serviceWorker.ready);
  if (!publicKey) { await api.subscribePush({ endpoint: 'demo', keys: { p256dh: '', auth: '' } }); return; }
  let sub = await reg.pushManager.getSubscription();
  if (!sub) sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
  const json = sub.toJSON();
  await api.subscribePush({ endpoint: json.endpoint, keys: { p256dh: json.keys.p256dh, auth: json.keys.auth } });
}

/** Turn notifications off for this device. */
export async function disablePush(api) {
  const reg = registration ?? (await navigator.serviceWorker?.getRegistration('/'));
  const sub = await reg?.pushManager.getSubscription();
  if (sub) {
    const endpoint = sub.endpoint;
    await sub.unsubscribe().catch(() => {});
    await api.unsubscribePush(endpoint).catch(() => {});
  } else await api.unsubscribePush('demo').catch(() => {});
}

// ---------- asset refresh ----------

const ASSETS_KEY = 'ts.assets';
const RELOADED_KEY = 'ts.assets.reloaded';
const read = (k, s = localStorage) => { try { return s.getItem(k); } catch { return null; } };
const write = (k, v, s = localStorage) => { try { s.setItem(k, v); } catch { /* private mode */ } };

/**
 * Compare the server's assetsVersion with the version the cache was built for. When it differs, ask the worker to
 * rebuild the cache from the network; resolves true when a reload is due (the caller picks a safe moment).
 */
export async function checkAssets(version) {
  if (!version || !swSupported) return false;
  const known = read(ASSETS_KEY);
  if (!known) { write(ASSETS_KEY, version); return false; }
  if (known === version) return false;
  if (read(RELOADED_KEY, sessionStorage) === version) return false; // reload once per version
  const reg = registration ?? (await navigator.serviceWorker.getRegistration('/'));
  const worker = reg?.active;
  if (!worker) { write(ASSETS_KEY, version); return false; }
  const ok = await new Promise((resolve) => {
    const ch = new MessageChannel();
    const timer = setTimeout(() => resolve(false), 30000);
    ch.port1.onmessage = (e) => { clearTimeout(timer); resolve(!!e.data?.ok); };
    worker.postMessage({ type: 'refresh', version }, [ch.port2]);
  });
  if (!ok) return false;
  write(ASSETS_KEY, version);
  write(RELOADED_KEY, version, sessionStorage);
  return true;
}
