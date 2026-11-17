// Pure timeline model: layout of a day, display model of a piece, categories, summaries, local day patches.
import { fmtRange, fmtDuration, fmtClock, localToMin, minToLocal, MINUTES_PER_DAY } from './time.js';

export const GROUPS = [
  { key: 'useful', name: 'Полезное' },
  { key: 'necessary', name: 'Необходимое' },
  { key: 'people', name: 'Люди' },
  { key: 'rest', name: 'Отдых' },
  { key: 'lost', name: 'Потерянное' },
  { key: 'none', name: 'Без категории' },
];
const GROUP_NAME = Object.fromEntries(GROUPS.map((g) => [g.key, g.name]));
export const groupName = (key) => GROUP_NAME[key] ?? GROUP_NAME.none;

export const STATUS_LABELS = {
  confirmed: 'Подтверждено', assumed: 'Предположено', questionable: 'Под вопросом', future: 'План',
};
export const SOURCE_LABELS = {
  user: 'Записано', extension: 'Продолжение занятия', schedule: 'По расписанию', background: 'Не записано', import: 'Импорт',
};
export const UNKNOWN_TITLE = 'Не записано';

/** Category key → { key, name, group, parent, parentName }. Unknown or null → group 'none'. */
export function categoryInfo(key, me) {
  if (!key) return { key: null, name: 'Без категории', group: 'none', parent: null, parentName: null };
  const list = me?.categories ?? [];
  const c = list.find((x) => x.key === key);
  if (!c) return { key, name: key, group: 'none', parent: null, parentName: null };
  const parent = c.parent ? list.find((x) => x.key === c.parent) : null;
  return { key: c.key, name: c.name, group: c.group ?? 'none', parent: c.parent ?? null, parentName: parent?.name ?? null };
}

/** Categories grouped for a picker: [{ group, name, categories: [{ key, name, parent, depth }] }]. */
export function categoriesByGroup(me) {
  const list = me?.categories ?? [];
  return GROUPS.filter((g) => g.key !== 'none')
    .map((g) => {
      const top = list.filter((c) => c.group === g.key && !c.parent);
      const categories = [];
      for (const c of top) {
        categories.push({ key: c.key, name: c.name, parent: null, depth: 0 });
        for (const ch of list.filter((x) => x.parent === c.key)) categories.push({ key: ch.key, name: ch.name, parent: c.key, depth: 1 });
      }
      return { group: g.key, name: g.name, categories };
    })
    .filter((g) => g.categories.length);
}

/** Kind of a piece for styling: confirmed | assumed | questionable | future. */
export const pieceKind = (p) => (p.future ? 'future' : p.status);

/** Stable key for keyed rendering and animations. */
export function pieceKey(p, prefix = 'm') {
  if (p.segment?.id) return `${prefix}:s:${p.segment.id}:${p.future ? 'f' : 'p'}`;
  // Only "now" moves: a past piece grows at its end, a future piece shrinks at its start — key by the fixed edge.
  return p.future
    ? `${prefix}:c:${p.source}:${p.category ?? ''}:${p.title}:e${p.endMin}:f`
    : `${prefix}:c:${p.source}:${p.category ?? ''}:${p.title}:${p.startMin}:p`;
}

/**
 * Display model of a piece.
 * Background + questionable is shown as «Не записано»: unknown time is not a judgement.
 */
export function describePiece(piece, me, { parallel = false } = {}) {
  const unknown = piece.source === 'background';
  const kind = pieceKind(piece);
  const title = unknown ? (piece.future ? 'Свободно' : UNKNOWN_TITLE) : piece.title || 'Без названия';
  const category = unknown ? categoryInfo(null, me) : categoryInfo(piece.category, me);
  const durationMin = Math.max(0, piece.endMin - piece.startMin);
  const past = !piece.future;
  const actions = {
    confirm: past && piece.status === 'assumed' && !piece.open,
    continue: past && !!piece.open,
    fill: past && piece.status === 'questionable' && !piece.open,
    edit: past,
    delete: past && !!piece.segment,
  };
  return {
    title, unknown, kind, status: piece.status, source: piece.source, future: !!piece.future, open: !!piece.open,
    parallel, category, group: category.group,
    startMin: piece.startMin, endMin: piece.endMin, durationMin,
    rangeText: fmtRange(piece.startMin, piece.endMin),
    durationText: fmtDuration(durationMin),
    startText: fmtClock(piece.startMin),
    endText: piece.open ? 'сейчас' : fmtClock(piece.endMin),
    statusLabel: STATUS_LABELS[kind],
    sourceLabel: SOURCE_LABELS[piece.source] ?? '',
    actions,
    segmentId: piece.segment?.id ?? null,
    activityId: piece.activityId ?? null,
  };
}

/**
 * Proportional layout of a day.
 * @param {object} day  Day from the API.
 * @param {object} opts { hourHeight = 60, minBlockHeight = 18, shortHeight = 40, minHit = 44, laneCount }
 * @returns {{ height, pxPerMin, dayMinutes, hours, blocks, parallel, lanes, nowY, yOf, minOf }}
 */
export function layoutDay(day, opts = {}) {
  const hourHeight = opts.hourHeight ?? 60;
  const minBlockHeight = opts.minBlockHeight ?? 18;
  const shortHeight = opts.shortHeight ?? 40;
  const minHit = opts.minHit ?? 44;
  const dayMinutes = day?.dayMinutes ?? MINUTES_PER_DAY;
  const ppm = hourHeight / 60;
  const height = dayMinutes * ppm;
  const yOf = (min) => min * ppm;
  const minOf = (y) => y / ppm;

  const hours = [];
  for (let h = 0; h * 60 <= dayMinutes; h++) hours.push({ hour: h, min: h * 60, label: h === 24 ? '00:00' : `${String(h).padStart(2, '0')}:00`, y: yOf(h * 60) });

  const seen = new Map();
  const mk = (p, i, prefix, extra) => {
    const y = yOf(p.startMin);
    const h = yOf(p.endMin) - y;
    let key = pieceKey(p, prefix);
    const n = seen.get(key) ?? 0;
    seen.set(key, n + 1);
    if (n) key += `#${n}`; // one segment can show as several pieces (e.g. a running activity: in norm, over norm)
    return {
      key, index: i, piece: p, kind: pieceKind(p),
      startMin: p.startMin, endMin: p.endMin, y, height: h,
      tiny: h < minBlockHeight, short: h < shortHeight,
      hitY: y, hitHeight: h, ...extra,
    };
  };

  const blocks = (day?.pieces ?? []).map((p, i) => mk(p, i, 'm'));
  expandHits(blocks, minHit, height);

  // Parallel track: greedy lanes for overlapping parallel entries.
  const laneEnds = [];
  // index stays the position in day.parallel (the list is laid out in start order).
  const parallel = (day?.parallel ?? []).map((p, i) => ({ p, i })).sort((a, b) => a.p.startMin - b.p.startMin).map(({ p, i }) => {
    let lane = laneEnds.findIndex((end) => end <= p.startMin);
    if (lane < 0) { lane = laneEnds.length; laneEnds.push(0); }
    laneEnds[lane] = p.endMin;
    return mk(p, i, 'p', { lane });
  });
  for (let lane = 0; lane < laneEnds.length; lane++) expandHits(parallel.filter((b) => b.lane === lane), minHit, height);

  const nowY = day?.nowMin == null ? null : yOf(day.nowMin);
  return { height, pxPerMin: ppm, hourHeight, dayMinutes, hours, blocks, parallel, lanes: laneEnds.length, nowY, yOf, minOf };
}

// Grow hit areas of small blocks to `minHit` px, splitting contested space between neighbours.
function expandHits(blocks, minHit, total) {
  for (const b of blocks) {
    if (b.height >= minHit) continue;
    const c = b.y + b.height / 2;
    b.hitY = Math.max(0, c - minHit / 2);
    b.hitHeight = Math.min(total, c + minHit / 2) - b.hitY;
  }
  for (let i = 0; i + 1 < blocks.length; i++) {
    const a = blocks[i];
    const b = blocks[i + 1];
    const aBottom = a.hitY + a.hitHeight;
    if (aBottom <= b.hitY) continue;
    const aGrew = aBottom > a.y + a.height;
    const bGrew = b.hitY < b.y;
    let edge;
    if (aGrew && bGrew) edge = (a.y + a.height / 2 + b.y + b.height / 2) / 2;
    else if (aGrew) edge = aBottom;
    else edge = b.hitY;
    a.hitHeight = Math.max(0, edge - a.hitY);
    const bBottom = b.hitY + b.hitHeight;
    b.hitY = edge;
    b.hitHeight = Math.max(0, bBottom - edge);
  }
}

/** Block under a y coordinate (uses hit areas). */
export function blockAt(blocks, y) {
  return blocks.find((b) => y >= b.hitY && y < b.hitY + b.hitHeight) ?? null;
}

/**
 * Scroll offset for a scroll container so that "now" sits at `ratio` of the viewport (0.4 by default).
 * Not today → the first non-sleep piece of the morning (or 07:00), with a small margin.
 * @param {object} layout     result of layoutDay
 * @param {number} viewport   visible height of the scroll container (px)
 * @param {object} opts       { ratio = 0.4, offsetTop = 0 (px from the container's top to the timeline's top), day }
 */
export function initialScrollTop(layout, viewport, opts = {}) {
  const ratio = opts.ratio ?? 0.4;
  const offsetTop = opts.offsetTop ?? 0;
  const max = Math.max(0, layout.height + offsetTop + (opts.offsetBottom ?? 0) - viewport);
  let target;
  if (layout.nowY != null) target = offsetTop + layout.nowY - viewport * ratio;
  else {
    const first = opts.day?.pieces?.find((p) => p.category !== 'sleep' && p.startMin >= 300);
    const min = first ? Math.min(first.startMin, 600) : 420;
    target = offsetTop + layout.yOf(min) - 24;
  }
  return Math.round(Math.min(max, Math.max(0, target)));
}

/** Scroll offset that puts a given day minute at `ratio` of the viewport. */
export function scrollTopFor(layout, min, viewport, { ratio = 0.4, offsetTop = 0 } = {}) {
  const max = Math.max(0, layout.height + offsetTop - viewport);
  return Math.round(Math.min(max, Math.max(0, offsetTop + layout.yOf(min) - viewport * ratio)));
}

// ---------- local projection of a day between server refreshes ----------

const isoAt = (day, min) => new Date(Date.parse(day.startUtc) + min * 60000).toISOString().replace('.000Z', 'Z');

/**
 * Move "now" forward on a day received earlier: the past piece that ended at the old "now" grows, future pieces
 * shrink, current.elapsedMin and totals follow. Pure; returns the same object when nothing changes.
 */
export function advanceDay(day, nowMin) {
  if (!day || day.nowMin == null || nowMin == null) return day;
  const target = Math.min(day.dayMinutes, Math.max(day.nowMin, Math.floor(nowMin)));
  const delta = target - day.nowMin;
  if (delta <= 0) return day;
  const old = day.nowMin;
  let grown = null;
  const pieces = [];
  let last = -1;
  day.pieces.forEach((p, i) => { if (!p.future && p.endMin === old) last = i; });
  for (const [i, p] of day.pieces.entries()) {
    if (i === last) {
      grown = { ...p, endMin: target, endUtc: isoAt(day, target) };
      pieces.push(grown);
    } else if (p.future || p.startMin >= old) {
      if (p.endMin <= target) continue;
      pieces.push(p.startMin < target ? { ...p, startMin: target, startUtc: isoAt(day, target) } : p);
    } else pieces.push(p);
  }
  const status = { ...day.status };
  let totals = day.totals;
  if (grown) {
    const k = `${grown.status}Min`;
    if (k in status) status[k] += delta;
    const cat = grown.category ?? null;
    const found = totals.some((t) => t.category === cat);
    totals = (found ? totals.map((t) => (t.category === cat ? { ...t, minutes: t.minutes + delta } : t)) : [...totals, { category: cat, minutes: delta }])
      .sort((a, b) => b.minutes - a.minutes);
  }
  const current = day.current ? { ...day.current, elapsedMin: day.current.elapsedMin + delta } : null;
  return { ...day, nowMin: target, nowUtc: isoAt(day, target), pieces, current, status, totals };
}

/**
 * Optimistic local change of a day while the request is in flight. The server's answer replaces it.
 * ops: { type: 'overlay', startMin, endMin, title, category, activityId, parallel }
 *      { type: 'confirm', startMin, endMin, includeQuestionable }
 *      { type: 'free', segmentId }            (delete)
 *      { type: 'continue' } | { type: 'stop', atMin } | { type: 'start', title, category, activityId, atMin }
 */
export function patchDay(day, op) {
  if (!day) return day;
  // Editing a stored segment: free its old place first, then draw it at the new one.
  if (op.type === 'overlay' && op.segmentId) return patchDay(patchDay(day, { type: 'free', segmentId: op.segmentId }), { ...op, segmentId: null });
  const now = day.nowMin ?? day.dayMinutes;
  let pieces = day.pieces.map((p) => ({ ...p }));
  let parallel = day.parallel.map((p) => ({ ...p }));
  let current = day.current;
  const unknown = (a, b) => ({ startMin: a, endMin: b, startUtc: isoAt(day, a), endUtc: isoAt(day, b), title: 'Залипание', category: 'wasted', status: 'questionable', source: 'background', future: false, open: false, activityId: null, segment: null });
  const cut = (list, a, b, insert) => {
    const out = [];
    for (const p of list) {
      if (p.endMin <= a || p.startMin >= b) { out.push(p); continue; }
      if (p.startMin < a) out.push({ ...p, endMin: a, endUtc: isoAt(day, a) });
      if (p.endMin > b) out.push({ ...p, startMin: b, startUtc: isoAt(day, b) });
    }
    if (insert) out.push(insert);
    return out.sort((x, y) => x.startMin - y.startMin || x.endMin - y.endMin);
  };
  switch (op.type) {
    case 'overlay': {
      const a = Math.max(0, op.startMin);
      const b = Math.min(day.dayMinutes, op.endMin);
      if (b <= a) break;
      const piece = { startMin: a, endMin: b, startUtc: isoAt(day, a), endUtc: isoAt(day, b), title: op.title, category: op.category ?? null, status: 'confirmed', source: 'user', future: false, open: false, activityId: op.activityId ?? null, segment: { id: `local:${a}`, startLocal: null, endLocal: null } };
      if (op.parallel) parallel = cut([], a, b, piece).concat(parallel).sort((x, y) => x.startMin - y.startMin);
      else pieces = cut(pieces, a, b, piece);
      break;
    }
    case 'confirm':
      pieces = pieces.map((p) => (p.future || p.endMin <= op.startMin || p.startMin >= op.endMin || p.open ? p
        : p.status === 'assumed' || (op.includeQuestionable && p.status === 'questionable') ? { ...p, status: 'confirmed' } : p));
      break;
    case 'free': {
      const hit = pieces.filter((p) => p.segment?.id === op.segmentId);
      parallel = parallel.filter((p) => p.segment?.id !== op.segmentId);
      for (const p of hit) pieces = cut(pieces, p.startMin, p.endMin, p.future ? null : unknown(p.startMin, Math.min(p.endMin, now)));
      if (current && current.segmentId === op.segmentId) current = null;
      break;
    }
    case 'continue':
      pieces = pieces.map((p) => (p.open ? { ...p, status: 'confirmed' } : p));
      break;
    case 'stop': {
      const at = op.atMin ?? now;
      pieces = pieces.map((p) => (p.open ? { ...p, open: false } : p));
      if (at < now) pieces = cut(pieces, at, now, unknown(at, now));
      current = null;
      break;
    }
    case 'start': {
      const at = Math.max(0, op.atMin ?? now);
      pieces = pieces.map((p) => (p.open ? { ...p, open: false } : p));
      const piece = { startMin: at, endMin: now, startUtc: isoAt(day, at), endUtc: isoAt(day, now), title: op.title, category: op.category ?? null, status: 'assumed', source: 'extension', future: false, open: true, activityId: op.activityId ?? null, segment: { id: 'local:start', startLocal: null, endLocal: null } };
      pieces = cut(pieces, at, now, piece);
      current = { segmentId: 'local:start', activityId: op.activityId ?? null, title: op.title, category: op.category ?? null, startUtc: isoAt(day, at), startLocal: null, elapsedMin: now - at, typicalMin: null };
      break;
    }
    default: break;
  }
  return { ...day, pieces, parallel, current };
}

// ---------- summaries ----------

/** Totals [{category, minutes}] → per category and per group with shares (0..1) of `base` (sum by default).
 *  opts: { unknownMin } moves that much of 'wasted' to a «Не записано» row; { wastedLabel } relabels 'wasted' and 'lost'. */
export function summarizeTotals(totals, me, base, opts = {}) {
  const sum = totals.reduce((s, t) => s + t.minutes, 0);
  const denom = base ?? sum;
  // Unknown (background) time is counted by the server under 'wasted'; when the caller knows how much of it is
  // unknown (from the pieces), move it to its own neutral row «Не записано» (group 'none', unknown: true).
  const unknownMin = Math.max(0, Math.min(opts.unknownMin ?? 0, totals.find((t) => t.category === BACKGROUND_CATEGORY)?.minutes ?? 0));
  const rows = totals
    .map((t) => (t.category === BACKGROUND_CATEGORY ? { ...t, minutes: t.minutes - unknownMin } : t))
    .filter((t) => t.minutes > 0);
  const row = (info, minutes, extra) => ({ ...info, label: info.name, unknown: false, minutes, share: denom ? minutes / denom : 0, durationText: fmtDuration(minutes), ...extra });
  const categories = rows.map((t) => row(categoryInfo(t.category, me), t.minutes, opts.wastedLabel && t.category === BACKGROUND_CATEGORY ? { label: opts.wastedLabel, includesUnknown: true } : null));
  if (unknownMin) categories.push(row({ key: null, name: UNKNOWN_TITLE, group: 'none', parent: null, parentName: null }, unknownMin, { unknown: true }));
  categories.sort((a, b) => b.minutes - a.minutes);
  const byGroup = new Map();
  for (const c of categories) if (!c.unknown) byGroup.set(c.group, (byGroup.get(c.group) ?? 0) + c.minutes);
  const group = (key, name, minutes, extra) => ({ group: key, name, label: name, unknown: false, minutes, share: denom ? minutes / denom : 0, durationText: fmtDuration(minutes), ...extra });
  const groups = GROUPS.filter((g) => byGroup.has(g.key)).map((g) => group(g.key, g.name, byGroup.get(g.key),
    opts.wastedLabel && g.key === 'lost' ? { label: `${g.name} (вкл. не записанное)`, includesUnknown: true } : null));
  if (unknownMin) groups.push(group('none', UNKNOWN_TITLE, unknownMin, { unknown: true }));
  groups.sort((a, b) => b.minutes - a.minutes);
  return { minutes: sum, categories, groups, unknownMin };
}

const BACKGROUND_CATEGORY = 'wasted';

/** Minutes of unknown time (questionable background pieces) in the past part of a day. */
export function unknownMinutes(day) {
  if (!day) return 0;
  const end = day.nowMin ?? day.dayMinutes;
  return day.pieces.reduce((s, p) => (!p.future && p.source === 'background' && p.status === 'questionable'
    ? s + Math.max(0, Math.min(p.endMin, end) - p.startMin) : s), 0);
}

/** { confirmedMin, assumedMin, questionableMin } → parts with shares. */
export function summarizeStatus(status) {
  const total = (status?.confirmedMin ?? 0) + (status?.assumedMin ?? 0) + (status?.questionableMin ?? 0);
  return {
    minutes: total,
    parts: ['confirmed', 'assumed', 'questionable'].map((k) => ({
      status: k, label: STATUS_LABELS[k], minutes: status?.[`${k}Min`] ?? 0,
      share: total ? (status?.[`${k}Min`] ?? 0) / total : 0, durationText: fmtDuration(status?.[`${k}Min`] ?? 0),
    })),
  };
}

/** Summary model of one day (from GET /api/day). */
export function daySummary(day, me) {
  if (!day) return null;
  const unknownMin = unknownMinutes(day);
  const status = summarizeStatus(day.status);
  const q = status.parts.find((p) => p.status === 'questionable');
  q.unknownMin = Math.min(unknownMin, q.minutes);
  return { date: day.date, isToday: day.isToday, totals: summarizeTotals(day.totals, me, undefined, { unknownMin }), status, parallelMin: day.parallelMin, unknownMin };
}

export const RANGE_UNKNOWN_NOTE = 'Не записанное время входит в «Под вопросом»; в итогах по категориям сервер пока считает его как «Залипание».';

/** Summary model of a range (from GET /api/summary): per-day stacked group data + totals. */
export function rangeSummary(summary, me) {
  if (!summary) return null;
  const days = summary.days.map((d) => {
    const t = summarizeTotals(d.totals, me, MINUTES_PER_DAY);
    const status = summarizeStatus(d.status);
    const confirmedShare = status.minutes ? (d.status?.confirmedMin ?? 0) / status.minutes : 0;
    // confirmedShare / confirmedText: how much of the day's past is confirmed — the meaningful per-day figure.
    return { date: d.date, minutes: t.minutes, groups: t.groups, categories: t.categories, status, parallelMin: d.parallelMin,
      confirmedShare, confirmedText: `${Math.round(confirmedShare * 100)}%` };
  });
  const maxMinutes = Math.max(1, ...days.map((d) => d.minutes));
  const status = summarizeStatus(summary.status);
  const q = status.parts.find((p) => p.status === 'questionable');
  q.label = 'Под вопросом (вкл. не записанное)';
  return {
    from: summary.from, to: summary.to, days, maxMinutes, status, note: RANGE_UNKNOWN_NOTE,
    // Server totals cannot separate unknown time: keep the numbers, label the 'wasted' row and the 'lost' group.
    totals: summarizeTotals(summary.totals, me, undefined, { wastedLabel: 'Залипание и не записанное' }),
  };
}

/** Pieces of a day that need attention: questionable past pieces (for "разобрать"). */
export const questionablePieces = (day) => (day?.pieces ?? []).filter((p) => !p.future && p.status === 'questionable' && !p.open);
/** Past assumed pieces that can be confirmed in one go. */
export const assumedPieces = (day) => (day?.pieces ?? []).filter((p) => !p.future && p.status === 'assumed' && !p.open);

/** Piece bounds as local strings: full segment bounds when stored (may cross days), else the clipped range. */
export function pieceBounds(piece, day) {
  if (piece.segment?.startLocal) {
    const start = piece.segment.startLocal;
    const end = piece.segment.endLocal;
    return { start, end, startMin: localToMin(start, day.date), endMin: end ? localToMin(end, day.date) : null, stored: true };
  }
  return { start: minToLocal(day.date, piece.startMin), end: minToLocal(day.date, piece.endMin), startMin: piece.startMin, endMin: piece.endMin, stored: false };
}
