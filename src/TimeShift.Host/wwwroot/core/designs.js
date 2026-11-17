// The designs (looks) of the app — the one list used by the chooser page and by Settings → Прочее → Вид.
// A design lives in /<id>/ and boots the core with createApp({ design: id }).

export const DESIGNS = [
  { id: 'a', name: 'Лист', hint: 'Бумажный ежедневник' },
  { id: 'b', name: 'Сетка', hint: 'Календарь iOS' },
  { id: 'c', name: 'Нить', hint: 'Тёмный типографический' },
];

export const DESIGN_KEY = 'ts.design';
const REOPEN_KEY = 'ts.reopen';

export const findDesign = (id) => DESIGNS.find((d) => d.id === id) ?? null;

/** The stored choice (a known id) or null. */
export function storedDesign() {
  let id = null;
  try { id = localStorage.getItem(DESIGN_KEY); } catch { /* private mode */ }
  return findDesign(id) ? id : null;
}

export function storeDesign(id) {
  try { localStorage.setItem(DESIGN_KEY, id); } catch { /* private mode */ }
}

/**
 * URL of a design keeping the flags that matter across a switch (demo, demo clock); one-shot flags
 * (fresh, login, from, choose, embed) are dropped.
 */
export function designUrl(id, search = location.search) {
  const from = new URLSearchParams(search);
  const keep = new URLSearchParams();
  for (const k of ['demo', 'at']) if (from.has(k)) keep.set(k, from.get(k));
  const q = keep.toString().replace(/%3A/gi, ':'); // keep at=15:40 readable
  return `/${id}/${q ? `?${q}` : ''}`;
}

/** Remember what to reopen after the next design boots (consumed once, within a minute). */
export function setReopen(intent) {
  try { sessionStorage.setItem(REOPEN_KEY, JSON.stringify({ ...intent, t: Date.now() })); } catch { /* ignore */ }
}
export function takeReopen() {
  let v = null;
  try {
    v = JSON.parse(sessionStorage.getItem(REOPEN_KEY) ?? 'null');
    sessionStorage.removeItem(REOPEN_KEY);
  } catch { v = null; }
  return v && Date.now() - v.t < 60000 ? v : null;
}
