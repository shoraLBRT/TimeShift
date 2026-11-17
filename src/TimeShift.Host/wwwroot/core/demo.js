// In-memory backend with the same interface as api.js (?demo=1). Same shapes and, where it matters, the same
// semantics as the server: layered timeline (segments → extension of the running activity → template → background),
// entries displace what they cover, undo reverts the last change. Data lives only in this tab.
import { ApiError } from './api.js';
import * as T from './time.js';

export const DEFAULT_CATEGORIES = [
  { key: 'work', name: 'Работа', group: 'useful', parent: null },
  { key: 'study', name: 'Учёба', group: 'useful', parent: null },
  { key: 'projects', name: 'Свои проекты', group: 'useful', parent: null },
  { key: 'sport', name: 'Спорт', group: 'useful', parent: null },
  { key: 'sleep', name: 'Сон', group: 'necessary', parent: null },
  { key: 'commute', name: 'Дорога', group: 'necessary', parent: null },
  { key: 'household', name: 'Быт', group: 'necessary', parent: null },
  { key: 'family', name: 'Семья', group: 'people', parent: null },
  { key: 'friends', name: 'Друзья', group: 'people', parent: null },
  { key: 'rest', name: 'Осознанный отдых', group: 'rest', parent: null },
  { key: 'wasted', name: 'Залипание', group: 'lost', parent: null },
  { key: 'wasted.short_video', name: 'Соцсети/короткие видео', group: 'lost', parent: 'wasted' },
  { key: 'wasted.youtube', name: 'YouTube', group: 'lost', parent: 'wasted' },
  { key: 'wasted.multiplayer', name: 'Мультиплеер-игры', group: 'lost', parent: 'wasted' },
  { key: 'wasted.lying', name: 'Валялся', group: 'lost', parent: 'wasted' },
  { key: 'wasted.other', name: 'Прочее', group: 'lost', parent: 'wasted' },
];

const SEED_ACTIVITIES = [
  ['Утро', 'household', 40], ['Дорога', 'commute', 40], ['Обед', 'household', 40], ['Тренировка', 'sport', 60],
  ['YouTube', 'wasted.youtube', 45], ['Ужин с семьёй', 'family', 50], ['Чтение', 'rest', 40],
  ['Пет-проект', 'projects', 60], ['Английский', 'study', 45], ['Встреча с друзьями', 'friends', 150],
  ['Dota 2', 'wasted.multiplayer', 60], ['Соцсети', 'wasted.short_video', 20], ['Уборка', 'household', 45],
  ['Созвон', 'work', 45], ['Прогулка', 'rest', 50],
];

const MIN = 60000;
const DEMO_PHRASE = 'Прогулка';
const BACKGROUND = 'Залипание';
const err = (status, code, message, field = null) => new ApiError(status, code, message, field);
const clone = (v) => (v == null ? v : structuredClone(v));
const iso = (ms) => new Date(ms).toISOString().replace('.000Z', 'Z');
const floorMin = (ms) => Math.floor(ms / MIN) * MIN;
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

function rng(seedText) {
  let s = 2166136261;
  for (const ch of seedText) s = Math.imul(s ^ ch.charCodeAt(0), 16777619);
  return () => {
    s = Math.imul(s ^ (s >>> 15), 2246822507);
    s = Math.imul(s ^ (s >>> 13), 3266489909);
    s ^= s >>> 16;
    return (s >>> 0) / 4294967296;
  };
}

/**
 * Offset (ms) that makes the clock read `at` ('HH:mm') today in the device zone; null when `at` is malformed.
 */
export function demoClockOffset(at, tz = T.deviceTimeZone()) {
  if (!/^\d{1,2}:\d{2}$/.test(at ?? '') || T.parseClock(at) >= 1440) return null;
  const clock = T.fmtClock(T.parseClock(at));
  return T.localToUtc(`${T.todayIn(tz)}T${clock}`, tz) - Date.now();
}

/**
 * @param {{ fresh?: boolean, login?: boolean, latency?: [number, number], onUnauthorized?: () => void,
 *           clockOffsetMs?: number }} opts  clockOffsetMs shifts the demo's "now" (see ?at=HH:mm)
 */
export function createDemoApi(opts = {}) {
  const tz = T.deviceTimeZone();
  let ids = 0;
  const id = (p) => `${p}${(++ids).toString(36)}`;
  const clock = () => Date.now() + (opts.clockOffsetMs ?? 0); // ?at=HH:mm shifts the demo clock
  const now = () => floorMin(clock());
  const toMs = (local) => T.localToUtc(local, tz);
  const toLocal = (ms) => T.utcToLocal(ms, tz);

  const db = {
    authed: !opts.login,
    me: {
      id: 'demo', displayName: 'Демо', timeZone: tz, onboarded: !opts.fresh, telegramLinked: false, quietUntilUtc: null,
      settings: { pingIntervalMin: 30, normFactor: 1.5, maxFactor: 2.0, maxUnansweredPings: 2, pingDuringBlocks: false, eveningReviewOffsetMin: 30, roundingMin: 5 },
      limits: { pingIntervalMin: { min: 5, max: 240 }, maxUnansweredPings: { min: 1, max: 10 }, eveningReviewOffsetMin: { min: 0, max: 180 }, roundingMin: [1, 5, 10, 15] },
      sleep: opts.fresh ? null : { start: '00:30', end: '08:30', weekend: null },
      blocks: opts.fresh ? [] : [{ id: 'b1', kind: 'fixed', title: 'Работа', category: 'work', days: [1, 2, 3, 4, 5], start: '10:00', end: '19:00' }],
      categories: DEFAULT_CATEGORIES,
    },
    acts: [],
    segs: [], // { id, start, end|null, title, activityId, category, status, source, parallel, confirmedUntil }
    pushSubs: 0,
  };
  const undo = [];

  // ---------- activities ----------
  const actByName = (name) => db.acts.find((a) => a.name.toLowerCase() === name.trim().toLowerCase());
  function ensureActivity({ activityId, title, category }) {
    if (activityId) {
      const a = db.acts.find((x) => x.id === activityId);
      if (!a) throw err(404, 'not_found', 'Такого занятия нет.');
      return a;
    }
    const t = (title ?? '').trim();
    if (!t || t.length > 60) throw err(422, 'validation', 'Название — от 1 до 60 символов.', 'title');
    let a = actByName(t);
    if (!a) {
      a = { id: id('a'), name: t, category: category ?? guessCategory(t), typicalMin: null, lastUsedUtc: null };
      db.acts.push(a);
    }
    return a;
  }
  const touch = (a, ms = now()) => { a.lastUsedUtc = iso(ms); };

  function guessCategory(t) {
    const s = t.toLowerCase();
    const rules = [[/работ|созвон|отчёт|отчет|митинг/, 'work'], [/учёб|учеб|курс|англ|лекц/, 'study'], [/проект|код/, 'projects'],
      [/трен|зал|бег|спорт|йог/, 'sport'], [/сон|спал/, 'sleep'], [/дорог|еду|метро|такси/, 'commute'], [/обед|ужин|завтрак|уборк|готов|душ/, 'household'],
      [/семь|мам|пап|дет/, 'family'], [/друз/, 'friends'], [/чтен|книг|прогул|медит/, 'rest'], [/youtube|ютуб/, 'wasted.youtube'],
      [/доту|dota|игр/, 'wasted.multiplayer'], [/соцсет|тикток|рилс|shorts/, 'wasted.short_video']];
    return rules.find(([re]) => re.test(s))?.[1] ?? null;
  }

  // ---------- template ----------
  function occurrences(fromMs, toMsExcl) {
    const me = db.me;
    const out = [];
    const first = T.addDays(T.todayIn(tz, fromMs), -1);
    const last = T.todayIn(tz, toMsExcl);
    for (let d = first; d <= last; d = T.addDays(d, 1)) {
      const wd = T.weekday(d);
      const add = (start, end, title, category, blockId) => {
        const crosses = T.parseClock(end) <= T.parseClock(start);
        const s = toMs(`${d}T${start}`);
        const e = toMs(`${crosses ? T.addDays(d, 1) : d}T${end}`);
        if (e > s && e > fromMs && s < toMsExcl) out.push({ s, e, title, category, blockId });
      };
      if (me.sleep) {
        const early = T.parseClock(me.sleep.start) < 720;
        const weekendNight = early ? wd >= 6 : wd === 5 || wd === 6;
        const sl = weekendNight && me.sleep.weekend ? me.sleep.weekend : me.sleep;
        add(sl.start, sl.end, 'Сон', 'sleep', null);
      }
      for (const b of me.blocks) if (b.days.includes(wd)) add(b.start, b.end, b.title, b.category ?? null, b.id);
    }
    return out.sort((a, b) => a.s - b.s);
  }

  // ---------- timeline ----------
  const running = () => db.segs.find((s) => s.end == null && !s.parallel) ?? null;
  const typicalOf = (s) => db.acts.find((a) => a.id === s.activityId)?.typicalMin ?? null;

  function buildDay(date) {
    const startMs = toMs(`${date}T00:00`);
    const endMs = toMs(`${T.addDays(date, 1)}T00:00`);
    const dayMinutes = Math.round((endMs - startMs) / MIN);
    const nowMs = now();
    const today = T.todayIn(tz, nowMs);
    const isToday = date === today;
    const nowMin = isToday ? Math.round((nowMs - startMs) / MIN) : null;
    const pastEnd = nowMs <= startMs ? 0 : nowMs >= endMs ? dayMinutes : Math.round((nowMs - startMs) / MIN);
    const occ = occurrences(startMs, endMs);
    const run = running();
    const closed = db.segs.filter((s) => !s.parallel && s.end != null && s.end > startMs && s.start < endMs).sort((a, b) => a.start - b.start);
    const { normFactor, maxFactor } = db.me.settings;
    const T0 = run ? typicalOf(run) : null;
    const normMin = run ? (T0 == null ? 0 : normFactor == null ? Infinity : T0 * normFactor) : 0;
    // Unknown typical duration: a 60-minute fallback, the whole extension questionable (as TimelineBuilder does).
    const maxMin = run ? (maxFactor == null ? Infinity : (T0 ?? 60) * maxFactor) : 0;

    const resolve = (t) => {
      const future = t >= nowMs;
      if (!future) {
        const seg = closed.find((s) => s.start <= t && t < s.end);
        if (seg) return { key: `s${seg.id}`, title: seg.title, category: seg.category, status: seg.status, source: seg.source, seg, open: false, activityId: seg.activityId };
        if (run && run.start <= t) {
          const o = occ.find((x) => x.s <= t && t < x.e);
          if (t < run.confirmedUntil) return { key: `r${run.id}c`, title: run.title, category: run.category, status: 'confirmed', source: 'user', seg: run, open: true, activityId: run.activityId };
          const el = (t - run.start) / MIN;
          if (el < maxMin) {
            // Questionable when over the norm, or when it ran into a block / sleep that began after the activity (#54).
            const status = (o && o.s > run.start) || el >= normMin ? 'questionable' : 'assumed';
            return { key: `r${run.id}${status}`, title: run.title, category: run.category, status, source: 'extension', seg: run, open: true, activityId: run.activityId };
          }
        }
      }
      const o = occ.find((x) => x.s <= t && t < x.e);
      if (o) return { key: `o${o.s}`, title: o.title, category: o.category, status: 'assumed', source: 'schedule', seg: null, open: false, activityId: null, future };
      return { key: 'bg', title: BACKGROUND, category: 'wasted', status: 'questionable', source: 'background', seg: null, open: false, activityId: null, future };
    };

    const pieces = [];
    let cur = null;
    for (let m = 0; m < dayMinutes; m++) {
      const t = startMs + m * MIN;
      const r = resolve(t);
      const future = t >= nowMs;
      const key = `${r.key}|${future}`;
      if (cur && cur.key === key) { cur.endMin = m + 1; continue; }
      cur = { key, startMin: m, endMin: m + 1, r, future };
      pieces.push(cur);
    }
    const out = pieces.map(({ startMin, endMin, r, future }) => ({
      startMin, endMin, startUtc: iso(startMs + startMin * MIN), endUtc: iso(startMs + endMin * MIN),
      title: r.title, category: r.category, status: r.status, source: r.source, future, open: r.open,
      activityId: r.activityId ?? null,
      segment: r.seg ? { id: r.seg.id, startLocal: toLocal(r.seg.start), endLocal: r.seg.end == null ? null : toLocal(r.seg.end) } : null,
    }));

    const parallel = db.segs.filter((s) => s.parallel && s.end > startMs && s.start < endMs).sort((a, b) => a.start - b.start).map((s) => {
      const a = Math.max(0, Math.round((s.start - startMs) / MIN));
      const b = Math.min(dayMinutes, Math.round((s.end - startMs) / MIN));
      return { startMin: a, endMin: b, startUtc: iso(startMs + a * MIN), endUtc: iso(startMs + b * MIN), title: s.title, category: s.category, status: s.status, source: s.source, future: false, open: false, activityId: s.activityId, segment: { id: s.id, startLocal: toLocal(s.start), endLocal: toLocal(s.end) } };
    });

    const totalsMap = new Map();
    const status = { confirmedMin: 0, assumedMin: 0, questionableMin: 0 };
    for (const p of out) {
      const len = Math.min(p.endMin, pastEnd) - p.startMin;
      if (len <= 0) continue;
      totalsMap.set(p.category, (totalsMap.get(p.category) ?? 0) + len);
      status[`${p.status}Min`] += len;
    }
    const parallelMin = parallel.reduce((s, p) => s + Math.max(0, Math.min(p.endMin, pastEnd) - p.startMin), 0);
    return {
      date, timeZone: tz, isToday, startUtc: iso(startMs), endUtc: iso(endMs), dayMinutes,
      nowUtc: iso(clock()), nowMin,
      pieces: out, parallel,
      current: run ? { segmentId: run.id, activityId: run.activityId, title: run.title, category: run.category, startUtc: iso(run.start), startLocal: toLocal(run.start), elapsedMin: Math.round((nowMs - run.start) / MIN), typicalMin: T0 } : null,
      totals: [...totalsMap].map(([category, minutes]) => ({ category, minutes })).sort((a, b) => b.minutes - a.minutes),
      status, parallelMin,
      attention: { pingPending: false, quietUntilUtc: db.me.quietUntilUtc },
    };
  }

  // ---------- mutations ----------
  function snapshot() {
    undo.push(clone({ segs: db.segs, acts: db.acts, me: db.me }));
    if (undo.length > 30) undo.shift();
  }

  function displace(a, b, exceptId = null) {
    const nowMs = now();
    const add = [];
    db.segs = db.segs.filter((s) => {
      if (s.parallel || s.id === exceptId) return true;
      const end = s.end ?? nowMs;
      if (end <= a || s.start >= b) return true;
      if (s.end == null) {
        if (s.start < a) add.push({ ...s, id: id('s'), end: a, status: 'confirmed', source: 'user' });
        s.start = b;
        s.confirmedUntil = Math.max(s.confirmedUntil, b);
        return true;
      }
      if (s.start < a && s.end > b) { add.push({ ...s, id: id('s'), start: b }); s.end = a; return true; }
      if (s.start < a) { s.end = a; return true; }
      if (s.end > b) { s.start = b; return true; }
      return false;
    });
    db.segs.push(...add);
  }

  function parseRange(start, end, { allowOpen = false } = {}) {
    const a = start ? toMs(start) : NaN;
    const b = end ? toMs(end) : allowOpen ? null : NaN;
    if (Number.isNaN(a)) throw err(422, 'validation', 'Укажи начало.', 'start');
    if (b !== null && Number.isNaN(b)) throw err(422, 'validation', 'Укажи конец.', 'end');
    if (b !== null && b <= a) throw err(422, 'validation', 'Конец должен быть позже начала.', 'end');
    if ((b ?? a) > now()) throw err(422, 'validation', 'Будущее не записывается', b === null ? 'start' : 'end');
    return [a, b];
  }

  function closeRunning(at) {
    const r = running();
    if (!r) return;
    if (at <= r.start) db.segs = db.segs.filter((s) => s !== r);
    else { r.end = at; r.status = 'confirmed'; r.source = 'user'; }
  }

  function startActivity(body) {
    const at = body.at ? parseRange(body.at, null, { allowOpen: true })[0] : now();
    const a = ensureActivity(body);
    closeRunning(at);
    if (at < now()) displace(at, now());
    db.segs.push({ id: id('s'), start: at, end: null, title: a.name, activityId: a.id, category: a.category, status: 'assumed', source: 'extension', parallel: false, confirmedUntil: at });
    touch(a);
    return a;
  }

  function addEntry(body) {
    const [a, b] = parseRange(body.start, body.end);
    const act = ensureActivity({ activityId: body.activityId, title: body.title, category: body.category });
    if (!body.parallel) displace(a, b);
    db.segs.push({ id: id('s'), start: a, end: b, title: act.name, activityId: act.id, category: body.category ?? act.category, status: 'confirmed', source: 'user', parallel: !!body.parallel, confirmedUntil: b });
    touch(act, b);
  }

  function confirmRange(a, b, includeQuestionable) {
    const nowMs = now();
    b = Math.min(b, nowMs);
    for (let d = T.todayIn(tz, a); toMs(`${d}T00:00`) < b; d = T.addDays(d, 1)) {
      const day = buildDay(d);
      const s0 = Date.parse(day.startUtc);
      for (const p of day.pieces) {
        if (p.future) continue;
        const ps = Math.max(a, s0 + p.startMin * MIN);
        const pe = Math.min(b, s0 + p.endMin * MIN);
        if (pe <= ps) continue;
        const ok = p.status === 'assumed' || (includeQuestionable && p.status === 'questionable');
        if (!ok) continue;
        if (p.open) { const r = running(); if (r) r.confirmedUntil = Math.max(r.confirmedUntil, pe); continue; }
        if (p.segment) continue;
        db.segs.push({ id: id('s'), start: ps, end: pe, title: p.title, activityId: p.activityId, category: p.category, status: 'confirmed', source: 'user', parallel: false, confirmedUntil: pe });
      }
    }
  }

  // ---------- validation of settings / template ----------
  function checkClock(c, field) {
    if (typeof c !== 'string' || !/^\d{2}:\d{2}$/.test(c) || T.parseClock(c) >= 1440) throw err(422, 'validation', 'Время в формате ЧЧ:ММ.', field);
  }
  function weekIntervals(list) {
    const out = [];
    for (const it of list) {
      const s0 = T.parseClock(it.start);
      let len = T.parseClock(it.end) - s0;
      if (len <= 0) len += 1440;
      for (const d of it.days) {
        const s = (d - 1) * 1440 + s0;
        const e = s + len;
        if (e <= 10080) out.push([s, e, it]);
        else { out.push([s, 10080, it]); out.push([0, e - 10080, it]); }
      }
    }
    return out.sort((x, y) => x[0] - y[0]);
  }
  function sleepItems(sleep) {
    if (!sleep) return [];
    const early = T.parseClock(sleep.start) < 720;
    const wk = early ? [6, 7] : [5, 6];
    if (!sleep.weekend) return [{ ...sleep, days: [1, 2, 3, 4, 5, 6, 7], title: 'Сон' }];
    return [{ start: sleep.start, end: sleep.end, days: [1, 2, 3, 4, 5, 6, 7].filter((d) => !wk.includes(d)), title: 'Сон' },
      { ...sleep.weekend, days: wk, title: 'Сон' }];
  }
  function checkOverlap(blocks, sleep) {
    const iv = weekIntervals([...sleepItems(sleep), ...blocks]);
    for (let i = 1; i < iv.length; i++) {
      if (iv[i][0] < iv[i - 1][1] && iv[i][2] !== iv[i - 1][2]) {
        const other = iv[i][2].isNew ? iv[i - 1][2] : iv[i][2];
        throw err(409, 'conflict', `Пересекается с «${other.title}».`);
      }
    }
  }
  function checkBlock(b) {
    const title = (b.title ?? '').trim();
    if (!title || title.length > 60) throw err(422, 'validation', 'Название — от 1 до 60 символов.', 'title');
    if (!Array.isArray(b.days) || !b.days.length || b.days.some((d) => d < 1 || d > 7)) throw err(422, 'validation', 'Выбери хотя бы один день.', 'days');
    checkClock(b.start, 'start'); checkClock(b.end, 'end');
    if (b.start === b.end) throw err(422, 'validation', 'Начало и конец совпадают.', 'end');
    return { kind: b.kind === 'custom' ? 'custom' : 'fixed', title, category: b.category ?? null, days: [...new Set(b.days)].sort(), start: b.start, end: b.end };
  }

  // ---------- seed ----------
  function seed() {
    for (const [name, category, typicalMin] of SEED_ACTIVITIES) db.acts.push({ id: id('a'), name, category, typicalMin, lastUsedUtc: null });
    const nowMs = now();
    const today = T.todayIn(tz, nowMs);
    const seg = (startMs, endMs, name, extra = {}) => {
      if (endMs <= startMs) return;
      const a = actByName(name);
      db.segs.push({ id: id('s'), start: startMs, end: endMs, title: a?.name ?? name, activityId: a?.id ?? null, category: a?.category ?? extra.category ?? null, status: 'confirmed', source: 'user', parallel: !!extra.parallel, confirmedUntil: endMs });
      if (a && (!a.lastUsedUtc || Date.parse(a.lastUsedUtc) < endMs)) a.lastUsedUtc = iso(endMs);
    };
    const at = (d, min) => toMs(T.minToLocal(d, Math.round(min)));
    const weekdayPool = [['Тренировка', 60, 90], ['Ужин с семьёй', 40, 60], ['YouTube', 30, 90], ['Пет-проект', 60, 120], ['Английский', 30, 45], ['Чтение', 30, 50], ['Dota 2', 60, 120], ['Соцсети', 15, 30], ['Уборка', 20, 40], ['Прогулка', 30, 60]];
    const weekendPool = [['Встреча с друзьями', 120, 200], ['Прогулка', 40, 90], ['Ужин с семьёй', 50, 80], ['YouTube', 40, 100], ['Dota 2', 60, 150], ['Уборка', 40, 70], ['Пет-проект', 60, 150], ['Чтение', 30, 60], ['Тренировка', 60, 90]];

    for (let i = 14; i >= 1; i--) {
      const d = T.addDays(today, -i);
      const r = rng(d);
      const pick = (n) => Math.floor(r() * n);
      const between = (a, b) => a + r() * (b - a);
      const weekend = T.isWeekend(d);
      const wake = weekend ? between(570, 630) : between(500, 525);
      if (r() < 0.6) seg(at(d, weekend ? between(60, 100) : between(15, 45)), at(d, wake), 'Сон', { category: 'sleep' });
      let t = wake;
      seg(at(d, t), at(d, t + 35), 'Утро'); t += 35 + (r() < 0.3 ? 10 : 0);
      if (!weekend) {
        seg(at(d, t), at(d, t + 40), 'Дорога');
        if (r() < 0.5) seg(at(d, 600), at(d, 1140), 'Работа', { category: 'work' });
        if (r() < 0.8) seg(at(d, 780), at(d, 780 + between(30, 45)), 'Обед', { parallel: true });
        seg(at(d, 1140), at(d, 1180), 'Дорога');
        t = 1180 + (r() < 0.5 ? between(15, 40) : 0);
      }
      const pool = weekend ? weekendPool : weekdayPool;
      const limit = 1440 + 20;
      while (t < limit - 20) {
        const [name, lo, hi] = pool[pick(pool.length)];
        const len = Math.min(between(lo, hi), limit - t);
        if (r() < 0.12) { t += between(20, 50); continue; } // a questionable gap
        seg(at(d, t), at(d, t + len), name);
        if (name === 'Ужин с семьёй' && r() < 0.4) seg(at(d, t + 5), at(d, t + 25), 'Соцсети', { parallel: true });
        t += len;
      }
    }

    // Today: morning confirmed, lunch parallel, a gap or two, a running activity started 50–90 min ago.
    const r = rng(today);
    const runStart = Math.floor((nowMs - (50 + Math.floor(r() * 40)) * MIN) / (5 * MIN)) * 5 * MIN;
    const clip = (s, e, name, extra) => seg(s, Math.min(e, runStart), name, extra);
    const weekend = T.isWeekend(today);
    const d = today;
    clip(at(d, 510), at(d, 550), 'Утро');
    if (!weekend) {
      clip(at(d, 550), at(d, 590), 'Дорога');
      if (at(d, 820) <= nowMs) seg(at(d, 780), at(d, 820), 'Обед', { parallel: true });
      clip(at(d, 1140), at(d, 1180), 'Дорога');
    } else clip(at(d, 690), at(d, 780), 'Прогулка');
    const hour = T.nowMinIn(tz, runStart) / 60;
    const runName = hour >= 8.5 && hour < 10 ? 'Чтение' : hour >= 10 && hour < 19 && !weekend ? 'Созвон' : hour >= 19 && hour < 23 ? 'Пет-проект' : 'YouTube';
    displace(runStart, nowMs);
    const a = actByName(runName);
    db.segs.push({ id: id('s'), start: runStart, end: null, title: a.name, activityId: a.id, category: a.category, status: 'assumed', source: 'extension', parallel: false, confirmedUntil: runStart });
    touch(a, nowMs);
  }
  if (!opts.fresh) seed();

  // ---------- messages ----------
  const proposals = new Map();
  function message(body) {
    if (!db.me.onboarded) throw err(409, 'conflict', 'Сначала закончи настройку.');
    const replies = [];
    if (body.button) {
      const [, kind, n] = body.button.split(':');
      const p = proposals.get(n);
      if (kind === 'ok' && p) { snapshot(); addEntry(p); proposals.delete(n); replies.push({ text: `Записал: ${p.label}.`, replacesSource: true, hasImage: false, buttons: [] }); }
      else if (kind === 'fix') replies.push({ text: 'Хорошо. Напиши, как было на самом деле — например, «вчера 19-21 зал».', replacesSource: true, hasImage: false, buttons: [] });
      else replies.push({ text: 'Хорошо.', replacesSource: true, hasImage: false, buttons: [] });
      return replies;
    }
    const text = (body.text ?? '').trim();
    if (!text) throw err(422, 'validation', 'Пустое сообщение.', 'text');
    const m = /^(вчера\s+)?(\d{1,2})(?::(\d{2}))?\s*[-–—]\s*(\d{1,2})(?::(\d{2}))?\s+(.{1,60})$/i.exec(text);
    if (m) {
      const date = m[1] ? T.addDays(T.todayIn(tz, now()), -1) : T.todayIn(tz, now());
      const s = Number(m[2]) * 60 + Number(m[3] ?? 0);
      let e = Number(m[4]) * 60 + Number(m[5] ?? 0);
      if (e <= s) e += 1440;
      const title = m[6].trim().replace(/^./, (c) => c.toUpperCase());
      const start = T.minToLocal(date, s);
      const end = T.minToLocal(date, e);
      if (toMs(end) > now()) return [{ text: 'Будущее не записывается — укажи время, которое уже прошло.', replacesSource: false, hasImage: false, buttons: [] }];
      const n = String(proposals.size + 1 + Math.floor(Math.random() * 1000));
      const label = `${m[1] ? 'вчера ' : ''}${T.fmtRange(s, e)} ${title}`;
      proposals.set(n, { start, end, title, label });
      return [{ text: `Записать так?\n${T.fmtRange(s, e)}  ${title}`, replacesSource: false, hasImage: false, buttons: [[{ label: '✅ Верно', data: `demo:ok:${n}` }, { label: '✏️ Поправить', data: `demo:fix:${n}` }]] }];
    }
    if (text.split(/\s+/).length <= 3 && !/\d/.test(text) && text.length <= 60) {
      snapshot();
      const a = startActivity({ title: text });
      return [{ text: `Начал «${a.name}» в ${T.fmtClock(T.nowMinIn(tz, now()))}.`, replacesSource: false, hasImage: false, buttons: [] }];
    }
    return [{ text: 'В демо я понимаю короткие названия занятий («Тренировка») и записи вида «вчера 19-21 зал». На сервере с подключённой моделью разберётся и такая фраза.', replacesSource: false, hasImage: false, buttons: [[{ label: 'Понятно', data: 'demo:ack:0' }]] }];
  }

  // ---------- transport ----------
  const [lo, hi] = opts.latency ?? [80, 200];
  async function call(fn, { anonymous = false } = {}) {
    await wait(lo + Math.random() * (hi - lo));
    if (!anonymous && !db.authed) { opts.onUnauthorized?.(); throw err(401, 'unauthorized', 'Нужно войти заново.'); }
    return clone(fn());
  }
  const mutate = (fn, view) => call(() => { snapshot(); try { fn(); } catch (e) { undo.pop(); throw e; } return { day: buildDay(view || T.todayIn(tz, now())) }; });
  const meOut = () => ({ ...db.me, quietUntilUtc: db.me.quietUntilUtc && Date.parse(db.me.quietUntilUtc) > clock() ? db.me.quietUntilUtc : null });

  return {
    demo: true,
    meta: () => call(() => ({ version: '0.0.0-demo', assetsVersion: null, webEnabled: true, authenticated: db.authed, onboarded: db.me.onboarded, llm: false, speech: opts.speech !== false, speechMaxSec: 120, push: { enabled: true, publicKey: null } }), { anonymous: true }),
    login: (key) => call(() => {
      if (!String(key ?? '').trim()) throw err(401, 'unauthorized', 'Неверный ключ.');
      db.authed = true; return null;
    }, { anonymous: true }),
    logout: () => call(() => { db.authed = false; return null; }, { anonymous: true }),

    me: () => call(meOut),
    saveSettings: (patch) => call(() => {
      const s = { ...db.me.settings };
      const lim = db.me.limits;
      const range = (k, { min, max }) => {
        if (!(k in patch)) return;
        const v = Number(patch[k]);
        if (!Number.isInteger(v) || v < min || v > max) throw err(422, 'validation', `Значение от ${min} до ${max}.`, k);
        s[k] = v;
      };
      range('pingIntervalMin', lim.pingIntervalMin); range('maxUnansweredPings', lim.maxUnansweredPings); range('eveningReviewOffsetMin', lim.eveningReviewOffsetMin);
      if ('roundingMin' in patch) { if (!lim.roundingMin.includes(patch.roundingMin)) throw err(422, 'validation', 'Округление: 1, 5, 10 или 15 минут.', 'roundingMin'); s.roundingMin = patch.roundingMin; }
      if ('pingDuringBlocks' in patch) s.pingDuringBlocks = !!patch.pingDuringBlocks;
      if ('normFactor' in patch) s.normFactor = patch.normFactor;
      if ('maxFactor' in patch) s.maxFactor = patch.maxFactor;
      const nf = s.normFactor ?? Infinity; const mf = s.maxFactor ?? Infinity;
      if (!(nf > 0) || mf < nf) throw err(422, 'validation', 'Предел должен быть не меньше нормы.', 'maxFactor');
      if ('timeZone' in patch) {
        try { new Intl.DateTimeFormat('en', { timeZone: patch.timeZone }); } catch { throw err(422, 'validation', 'Неизвестный часовой пояс.', 'timeZone'); }
        db.me.timeZone = patch.timeZone;
      }
      if ('displayName' in patch) db.me.displayName = String(patch.displayName).slice(0, 60);
      db.me.settings = s;
      return meOut();
    }),
    setSleep: (sleep) => call(() => {
      checkClock(sleep?.start, 'start'); checkClock(sleep?.end, 'end');
      if (sleep.weekend) { checkClock(sleep.weekend.start, 'weekend.start'); checkClock(sleep.weekend.end, 'weekend.end'); }
      const next = { start: sleep.start, end: sleep.end, weekend: sleep.weekend ? { start: sleep.weekend.start, end: sleep.weekend.end } : null };
      checkOverlap(db.me.blocks, next);
      db.me.sleep = next;
      return meOut();
    }),
    addBlock: (b) => call(() => {
      const nb = { id: id('b'), ...checkBlock(b) };
      checkOverlap([...db.me.blocks, { ...nb, isNew: true }], db.me.sleep);
      db.me.blocks = [...db.me.blocks, nb];
      return meOut();
    }),
    updateBlock: (bid, b) => call(() => {
      if (!db.me.blocks.some((x) => x.id === bid)) throw err(404, 'not_found', 'Такого блока нет.');
      const nb = { id: bid, ...checkBlock(b) };
      checkOverlap(db.me.blocks.map((x) => (x.id === bid ? { ...nb, isNew: true } : x)), db.me.sleep);
      db.me.blocks = db.me.blocks.map((x) => (x.id === bid ? nb : x));
      return meOut();
    }),
    deleteBlock: (bid) => call(() => {
      if (!db.me.blocks.some((x) => x.id === bid)) throw err(404, 'not_found', 'Такого блока нет.');
      db.me.blocks = db.me.blocks.filter((x) => x.id !== bid);
      return meOut();
    }),
    completeOnboarding: () => call(() => {
      if (!db.me.sleep) throw err(409, 'conflict', 'Сначала укажи время сна.');
      db.me.onboarded = true;
      return meOut();
    }),

    day: (date) => call(() => {
      if (!T.isDate(date)) throw err(400, 'bad_request', 'Неверная дата.');
      return buildDay(date);
    }),
    summary: (from, to) => call(() => {
      const n = T.diffDays(from, to) + 1;
      if (n < 1 || n > 31) throw err(422, 'validation', 'Не больше 31 дня.', 'to');
      const days = [];
      const totals = new Map();
      const status = { confirmedMin: 0, assumedMin: 0, questionableMin: 0 };
      for (let i = 0; i < n; i++) {
        const day = buildDay(T.addDays(from, i));
        days.push({ date: day.date, totals: day.totals, status: day.status, parallelMin: day.parallelMin });
        for (const t of day.totals) totals.set(t.category, (totals.get(t.category) ?? 0) + t.minutes);
        for (const k of Object.keys(status)) status[k] += day.status[k];
      }
      return { from, to, days, totals: [...totals].map(([category, minutes]) => ({ category, minutes })).sort((a, b) => b.minutes - a.minutes), status };
    }),

    start: (body, view) => mutate(() => startActivity(body ?? {}), view),
    stop: (body, view) => mutate(() => {
      const r = running();
      if (!r) throw err(409, 'conflict', 'Сейчас ничего не идёт.');
      const at = body?.at ? parseRange(body.at, null, { allowOpen: true })[0] : now();
      if (at < r.start) throw err(422, 'validation', 'Раньше начала занятия.', 'at');
      closeRunning(at);
    }, view),
    continue: (view) => mutate(() => {
      const r = running();
      if (!r) throw err(409, 'conflict', 'Сейчас ничего не идёт.');
      r.confirmedUntil = now();
    }, view),
    addEntry: (body, view) => mutate(() => addEntry(body ?? {}), view),
    updateEntry: (sid, patch, view) => mutate(() => {
      const s = db.segs.find((x) => x.id === sid);
      if (!s) throw err(404, 'not_found', 'Эта запись уже изменилась.');
      if (s.end == null && patch.end) {
        // Editing the end of the running activity = stopping it then.
        const [a, b] = parseRange(patch.start ?? toLocal(s.start), patch.end);
        s.start = a; s.end = b; s.status = 'confirmed'; s.source = 'user';
      } else {
        const [a, b] = parseRange(patch.start ?? toLocal(s.start), s.end == null ? null : patch.end ?? toLocal(s.end), { allowOpen: s.end == null });
        s.start = a; if (b != null) s.end = b;
        if (s.end == null) s.confirmedUntil = Math.max(s.confirmedUntil, a);
      }
      if (patch.title && patch.title.trim() !== s.title) {
        const act = ensureActivity({ title: patch.title, category: patch.category });
        s.title = act.name; s.activityId = act.id; s.category = patch.category ?? act.category;
      } else if ('category' in patch) s.category = patch.category;
      if (s.end != null) { s.status = 'confirmed'; s.source = 'user'; }
      if (!s.parallel) displace(s.start, s.end ?? now(), s.id);
    }, view),
    deleteEntry: (sid, view) => mutate(() => {
      if (!db.segs.some((x) => x.id === sid)) throw err(404, 'not_found', 'Эта запись уже удалена.');
      db.segs = db.segs.filter((x) => x.id !== sid);
    }, view),
    confirm: (body, view) => mutate(() => {
      const [a, b] = [toMs(body.start), toMs(body.end)];
      if (!(b > a)) throw err(422, 'validation', 'Конец должен быть позже начала.', 'end');
      confirmRange(a, b, !!body.includeQuestionable);
    }, view),
    undo: (view) => call(() => {
      const prev = undo.pop();
      if (!prev) return { result: 'nothing', day: buildDay(view || T.todayIn(tz, now())) };
      db.segs = prev.segs; db.acts = prev.acts; db.me = prev.me;
      return { result: 'undone', day: buildDay(view || T.todayIn(tz, now())) };
    }),
    quiet: (minutes, view) => mutate(() => {
      const m = Number(minutes);
      if (m !== 0 && !(m >= 5 && m <= 1440)) throw err(422, 'validation', 'От 5 минут до суток.', 'minutes');
      db.me.quietUntilUtc = m ? iso(clock() + m * MIN) : null;
    }, view),

    activities: () => call(() => db.acts.slice().sort((a, b) => (b.lastUsedUtc ?? '').localeCompare(a.lastUsedUtc ?? '')).slice(0, 200)),
    updateActivity: (aid, patch) => call(() => {
      const a = db.acts.find((x) => x.id === aid);
      if (!a) throw err(404, 'not_found', 'Такого занятия нет.');
      if ('name' in patch) {
        const n = String(patch.name ?? '').trim();
        if (!n || n.length > 60) throw err(422, 'validation', 'Название — от 1 до 60 символов.', 'name');
        const dup = actByName(n);
        if (dup && dup !== a) throw err(409, 'conflict', `Занятие «${dup.name}» уже есть.`);
        a.name = n;
        for (const s of db.segs) if (s.activityId === a.id) s.title = n;
      }
      if ('category' in patch) a.category = patch.category;
      if ('typicalMin' in patch) {
        if (patch.typicalMin != null && !(Number.isInteger(patch.typicalMin) && patch.typicalMin >= 1 && patch.typicalMin <= 1440)) throw err(422, 'validation', 'От 1 до 1440 минут.', 'typicalMin');
        a.typicalMin = patch.typicalMin;
      }
      return a;
    }),

    message: (body, view) => call(() => ({ replies: message(body ?? {}), day: buildDay(view || T.todayIn(tz, now())) })),
    // The demo does not listen: whatever was said, it "hears" the same phrase.
    voice: (wav, view) => call(() => ({
      replies: [{ text: `🎙 «${DEMO_PHRASE}»`, replacesSource: false, hasImage: false, buttons: [] }, ...message({ text: DEMO_PHRASE })],
      day: buildDay(view || T.todayIn(tz, now())),
    })),

    pushInfo: () => call(() => ({ enabled: true, publicKey: null, subscriptions: db.pushSubs })),
    subscribePush: () => call(() => { db.pushSubs = 1; return null; }),
    unsubscribePush: () => call(() => { db.pushSubs = 0; return null; }),
    testPush: () => call(() => {
      if (!db.pushSubs) throw err(409, 'conflict', 'На этом устройстве уведомления не включены.');
      const r = running();
      const payload = r
        ? { kind: 'ping', title: `Всё ещё «${r.title}»?`, body: `Идёт ${T.fmtDuration((now() - r.start) / MIN)}`, tag: 'ts-ping', url: '/?from=ping', actions: [{ action: 'continue', title: 'Продолжаю' }, { action: 'stop', title: 'Закончил' }] }
        : { kind: 'test', title: 'TimeShift', body: 'Проверка уведомлений', tag: 'ts-test', url: '/' };
      navigator.serviceWorker?.getRegistration?.('/').then((reg) => reg?.active?.postMessage({ type: 'demo-push', payload })).catch(() => {});
      return null;
    }),
  };
}
