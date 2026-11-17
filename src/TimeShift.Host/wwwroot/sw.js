// TimeShift service worker (scope '/').
// Development hosts: network first. Elsewhere: cache first for same-origin GET outside /api/, with an explicit
// refresh protocol driven by the page (meta.assetsVersion). /api/ is never cached.
const PREFIX = 'ts-assets-';
const DEV = ['localhost', '127.0.0.1', '[::1]'].includes(self.location.hostname);
let active = null; // name of the current asset cache

async function currentCache() {
  if (active) return active;
  const names = (await caches.keys()).filter((n) => n.startsWith(PREFIX)).sort();
  active = names.at(-1) ?? `${PREFIX}0-initial`;
  return active;
}

self.addEventListener('install', (e) => {
  e.waitUntil((async () => {
    const c = await caches.open(await currentCache());
    // Only the shell; every design (/<id>/…) is cached on first use, so switching works offline once each was opened.
    await c.addAll(['/', '/core/designs.js', '/manifest.webmanifest', '/icons/icon-192.png']).catch(() => {});
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', (e) => {
  e.waitUntil(self.clients.claim());
});

const cacheable = (req, url) => req.method === 'GET' && url.origin === self.location.origin && !url.pathname.startsWith('/api/') && url.pathname !== '/sw.js';

self.addEventListener('fetch', (e) => {
  const req = e.request;
  const url = new URL(req.url);
  if (!cacheable(req, url)) return;
  e.respondWith(DEV ? networkFirst(req) : cacheFirst(req));
});

async function put(req, res) {
  if (!res || !res.ok || res.type === 'opaque') return;
  const c = await caches.open(await currentCache());
  await c.put(req, res);
}

async function networkFirst(req) {
  try {
    const res = await fetch(req, { cache: 'no-store' });
    put(req, res.clone());
    return res;
  } catch {
    return (await fallback(req)) ?? Response.error();
  }
}

async function cacheFirst(req) {
  const c = await caches.open(await currentCache());
  const hit = await c.match(req, { ignoreSearch: req.mode === 'navigate' });
  if (hit) return hit;
  try {
    const res = await fetch(req);
    put(req, res.clone());
    return res;
  } catch {
    return (await fallback(req)) ?? Response.error();
  }
}

async function fallback(req) {
  const c = await caches.open(await currentCache());
  const hit = await c.match(req, { ignoreSearch: true });
  if (hit) return hit;
  if (req.mode !== 'navigate') return null;
  // A page never opened before (e.g. a design not used yet): say so. Falling back to '/' would bounce back to the
  // stored design and loop.
  const path = new URL(req.url).pathname;
  if (path === '/' || path === '/index.html') return (await c.match('/')) ?? null;
  return new Response(OFFLINE_PAGE, { status: 503, headers: { 'Content-Type': 'text/html; charset=utf-8' } });
}

const OFFLINE_PAGE = `<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark"><title>Нет сети</title>
<body style="font:17px/1.45 system-ui,sans-serif;padding:48px 24px;max-width:32em;margin:auto">
<p><b>Нет сети</b></p><p>Этот вид ещё не открывался на устройстве, поэтому без сети его не показать.</p>
<p><a href="javascript:history.back()">Назад</a></p></body></html>`;

// Refresh protocol: { type: 'refresh', version } with a MessagePort → rebuild every cached file from the network
// into a new cache, switch to it, drop the old one, answer { ok }.
self.addEventListener('message', (e) => {
  const m = e.data ?? {};
  if (m.type === 'refresh') e.waitUntil(refresh(m.version, e.ports[0]));
  else if (m.type === 'demo-push') e.waitUntil(show(m.payload ?? {}));
});

async function refresh(version, port) {
  try {
    const oldName = await currentCache();
    const old = await caches.open(oldName);
    const nextName = `${PREFIX}${Date.now()}-${String(version).replace(/[^\w.-]/g, '')}`;
    const next = await caches.open(nextName);
    const reqs = await old.keys();
    await Promise.all(reqs.map(async (r) => {
      try {
        const res = await fetch(r.url, { cache: 'reload', credentials: 'same-origin' });
        if (res.ok) await next.put(r.url, res);
      } catch { /* keep going: missing files are fetched on demand */ }
    }));
    active = nextName;
    for (const n of await caches.keys()) if (n.startsWith(PREFIX) && n !== nextName) await caches.delete(n);
    port?.postMessage({ ok: true });
  } catch {
    port?.postMessage({ ok: false });
  }
}

// ---------- push ----------
async function show(p) {
  const title = p.title || 'TimeShift';
  await self.registration.showNotification(title, {
    body: p.body || '',
    tag: p.tag || 'ts',
    renotify: true,
    icon: '/icons/icon-192.png',
    badge: '/icons/icon-192.png',
    data: { url: p.url || '/', kind: p.kind || 'ping' },
    actions: Array.isArray(p.actions) ? p.actions.slice(0, 2) : [],
  });
}

self.addEventListener('push', (e) => {
  let p = {};
  try { p = e.data ? e.data.json() : {}; } catch { p = { title: 'TimeShift', body: e.data?.text() ?? '' }; }
  e.waitUntil(show(p));
});

self.addEventListener('notificationclick', (e) => {
  const n = e.notification;
  const data = n.data || {};
  n.close();
  e.waitUntil((async () => {
    if (e.action === 'continue' || e.action === 'stop') {
      try {
        const res = await fetch(`/api/now/${e.action}`, {
          method: 'POST', credentials: 'same-origin',
          headers: { 'X-TimeShift': '1', 'Content-Type': 'application/json', Accept: 'application/json' },
          body: e.action === 'stop' ? '{}' : undefined,
        });
        if (res.ok) {
          for (const c of await self.clients.matchAll({ type: 'window', includeUncontrolled: true })) c.postMessage({ type: 'changed' });
          return;
        }
      } catch { /* fall through: open the app */ }
    }
    const wins = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const win = wins.find((w) => new URL(w.url).origin === self.location.origin);
    if (win) {
      await win.focus();
      win.postMessage({ type: 'ping', kind: data.kind });
      return;
    }
    await self.clients.openWindow(data.url || '/?from=ping');
  })());
});
