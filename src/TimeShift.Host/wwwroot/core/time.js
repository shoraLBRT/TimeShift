// Time and date helpers (Russian). Pure functions, no DOM.
// Conventions follow docs/web-api.md: dates are 'YYYY-MM-DD', local times 'YYYY-MM-DDTHH:mm', clocks 'HH:mm'.

export const MINUTES_PER_DAY = 1440;

export const MONTHS = ['январь', 'февраль', 'март', 'апрель', 'май', 'июнь', 'июль', 'август', 'сентябрь', 'октябрь', 'ноябрь', 'декабрь'];
export const MONTHS_GEN = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];
export const MONTHS_SHORT = ['янв', 'фев', 'мар', 'апр', 'мая', 'июн', 'июл', 'авг', 'сен', 'окт', 'ноя', 'дек'];
/** ISO weekdays: index 0 = Monday (1) … 6 = Sunday (7). */
export const WEEKDAYS = ['понедельник', 'вторник', 'среда', 'четверг', 'пятница', 'суббота', 'воскресенье'];
export const WEEKDAYS_SHORT = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'вс'];

export const pad2 = (n) => String(n).padStart(2, '0');
export const capitalize = (s) => (s ? s[0].toUpperCase() + s.slice(1) : s);

/** Russian plural: plural(5, 'минута', 'минуты', 'минут') → 'минут'. */
export function plural(n, one, few, many) {
  const a = Math.abs(n) % 100;
  const b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b === 1) return one;
  if (b >= 2 && b <= 4) return few;
  return many;
}

// ---------- clocks and durations ----------

/** Minutes from midnight → 'HH:mm'. Values ≥ 1440 or < 0 wrap around the day. */
export function fmtClock(min) {
  const m = ((Math.round(min) % MINUTES_PER_DAY) + MINUTES_PER_DAY) % MINUTES_PER_DAY;
  return `${pad2(Math.floor(m / 60))}:${pad2(m % 60)}`;
}

/** 'HH:mm' → minutes from midnight (NaN when malformed). */
export function parseClock(clock) {
  const m = /^(\d{1,2}):(\d{2})/.exec(clock ?? '');
  if (!m) return NaN;
  return Number(m[1]) * 60 + Number(m[2]);
}

/** 150 → '2 ч 30 мин', 45 → '45 мин', 120 → '2 ч'. */
export function fmtDuration(min) {
  const t = Math.max(0, Math.round(min));
  const h = Math.floor(t / 60);
  const m = t % 60;
  if (h && m) return `${h} ч ${m} мин`;
  if (h) return `${h} ч`;
  return `${m} мин`;
}

/** 80 → '1:20', 5 → '0:05'. */
export function fmtCompact(min) {
  const t = Math.max(0, Math.round(min));
  return `${Math.floor(t / 60)}:${pad2(t % 60)}`;
}

/** Day minutes → '09:00–10:30'. An end at or past the next midnight shows the clock of the next day. */
export function fmtRange(startMin, endMin) {
  return `${fmtClock(startMin)}–${fmtClock(endMin)}`;
}

// ---------- dates (strings, calendar arithmetic in UTC so no device zone leaks in) ----------

function dateToUtc(date) {
  const [y, m, d] = date.split('-').map(Number);
  return Date.UTC(y, m - 1, d);
}
function utcToDate(ms) {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}-${pad2(d.getUTCMonth() + 1)}-${pad2(d.getUTCDate())}`;
}

export const isDate = (s) => typeof s === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(s);
export const addDays = (date, n) => utcToDate(dateToUtc(date) + n * 86400000);
/** b − a in days. */
export const diffDays = (a, b) => Math.round((dateToUtc(b) - dateToUtc(a)) / 86400000);
/** ISO weekday 1 (Monday) … 7 (Sunday). */
export function weekday(date) {
  const w = new Date(dateToUtc(date)).getUTCDay();
  return w === 0 ? 7 : w;
}
/** Monday of the week containing `date`. */
export const weekStart = (date) => addDays(date, 1 - weekday(date));
/** The 7 dates Monday…Sunday of the week containing `date`. */
export const weekDates = (date) => Array.from({ length: 7 }, (_, i) => addDays(weekStart(date), i));
/** `count` dates ending with `date` (inclusive), oldest first. */
export const lastDays = (date, count = 7) => Array.from({ length: count }, (_, i) => addDays(date, i - count + 1));
export const isWeekend = (date) => weekday(date) >= 6;

export function dateParts(date) {
  const [y, m, d] = date.split('-').map(Number);
  return { year: y, month: m, day: d, weekday: weekday(date) };
}

/** «Сегодня», «Вчера», «Завтра», otherwise «Вторник, 17 ноября» (+ year when not the current one). */
export function fmtDateLabel(date, today) {
  if (today) {
    const d = diffDays(today, date);
    if (d === 0) return 'Сегодня';
    if (d === -1) return 'Вчера';
    if (d === 1) return 'Завтра';
  }
  return capitalize(fmtDateLong(date, today));
}

/** 'вторник, 17 ноября' (+ ' 2025' when the year differs from today's). */
export function fmtDateLong(date, today) {
  const p = dateParts(date);
  const year = today && today.slice(0, 4) !== date.slice(0, 4) ? ` ${p.year}` : '';
  return `${WEEKDAYS[p.weekday - 1]}, ${p.day} ${MONTHS_GEN[p.month - 1]}${year}`;
}

/** '17 ноября' */
export function fmtDayMonth(date) {
  const p = dateParts(date);
  return `${p.day} ${MONTHS_GEN[p.month - 1]}`;
}

/** 'вт, 17 ноя' */
export function fmtDateShort(date) {
  const p = dateParts(date);
  return `${WEEKDAYS_SHORT[p.weekday - 1]}, ${p.day} ${MONTHS_SHORT[p.month - 1]}`;
}

/** Short label for chips and tabs: «Сегодня», «Вчера» or 'вт, 17 ноя'. */
export function fmtDateChip(date, today) {
  if (today) {
    const d = diffDays(today, date);
    if (d === 0) return 'Сегодня';
    if (d === -1) return 'Вчера';
    if (d === 1) return 'Завтра';
  }
  return fmtDateShort(date);
}

/** 'Ноябрь 2026' */
export function fmtMonthYear(date) {
  const p = dateParts(date);
  return `${capitalize(MONTHS[p.month - 1])} ${p.year}`;
}

/** '11–17 ноября' or '28 октября – 3 ноября'. */
export function fmtDateSpan(from, to) {
  const a = dateParts(from);
  const b = dateParts(to);
  if (a.month === b.month && a.year === b.year) return `${a.day}–${b.day} ${MONTHS_GEN[b.month - 1]}`;
  return `${a.day} ${MONTHS_GEN[a.month - 1]} – ${b.day} ${MONTHS_GEN[b.month - 1]}`;
}

// ---------- local time strings ----------

/** 'YYYY-MM-DDTHH:mm' → { date, min }. */
export function parseLocal(local) {
  if (!local) return null;
  return { date: local.slice(0, 10), min: parseClock(local.slice(11, 16)) };
}

/**
 * Day minute (may be < 0 or ≥ 1440) of `date` → local time string. Wall-clock arithmetic: exact except on the two
 * days a year when the clocks change, where the pieces' own startUtc/endUtc should be preferred (see utcToLocal).
 */
export function minToLocal(date, min) {
  const dayShift = Math.floor(min / MINUTES_PER_DAY);
  const m = min - dayShift * MINUTES_PER_DAY;
  return `${addDays(date, dayShift)}T${fmtClock(m)}`;
}

/** Local time string → minutes relative to the start of `date` (negative for the day before, ≥ 1440 after). */
export function localToMin(local, date) {
  const p = parseLocal(local);
  if (!p) return NaN;
  return diffDays(date, p.date) * MINUTES_PER_DAY + p.min;
}

// ---------- time zones ----------

const formatters = new Map();
function formatter(tz) {
  let f = formatters.get(tz);
  if (!f) {
    f = new Intl.DateTimeFormat('en-US', {
      timeZone: tz, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit',
    });
    formatters.set(tz, f);
  }
  return f;
}

/** Wall-clock parts of an instant in a zone. */
export function zonedParts(ms, tz) {
  const out = {};
  for (const p of formatter(tz).formatToParts(new Date(ms))) out[p.type] = p.value;
  const hour = Number(out.hour) % 24;
  return {
    date: `${out.year}-${out.month}-${out.day}`,
    hour, minute: Number(out.minute), second: Number(out.second),
    min: hour * 60 + Number(out.minute),
  };
}

/** Offset of the zone at an instant, in minutes (Moscow → 180). */
export function tzOffsetMin(ms, tz) {
  const p = zonedParts(ms, tz);
  const [y, m, d] = p.date.split('-').map(Number);
  const asUtc = Date.UTC(y, m - 1, d, p.hour, p.minute, p.second);
  return Math.round((asUtc - Math.floor(ms / 1000) * 1000) / 60000);
}

/** Instant (ms) → 'YYYY-MM-DDTHH:mm' in the zone. */
export function utcToLocal(msOrIso, tz) {
  const ms = typeof msOrIso === 'number' ? msOrIso : Date.parse(msOrIso);
  const p = zonedParts(ms, tz);
  return `${p.date}T${pad2(p.hour)}:${pad2(p.minute)}`;
}

/** 'YYYY-MM-DDTHH:mm' in the zone → instant (ms). Times inside a DST gap move forward. */
export function localToUtc(local, tz) {
  const [date, clock] = local.split('T');
  const [y, m, d] = date.split('-').map(Number);
  const min = parseClock(clock);
  const wall = Date.UTC(y, m - 1, d, Math.floor(min / 60), min % 60);
  let t = wall - tzOffsetMin(wall, tz) * 60000;
  const off2 = tzOffsetMin(t, tz);
  if (wall - off2 * 60000 !== t) t = wall - off2 * 60000;
  return t;
}

/** Local date in the zone at an instant. */
export const todayIn = (tz, ms = Date.now()) => zonedParts(ms, tz).date;
/** Minutes since local midnight in the zone at an instant. */
export const nowMinIn = (tz, ms = Date.now()) => zonedParts(ms, tz).min;

/** The device time zone (IANA). */
export function deviceTimeZone() {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
}

/** All IANA zones the browser knows (falls back to a short list). */
export function timeZones() {
  try {
    const list = Intl.supportedValuesOf('timeZone');
    if (list?.length) return list.includes('UTC') ? list : [...list, 'UTC'];
  } catch { /* old engine */ }
  return ['Europe/Kaliningrad', 'Europe/Moscow', 'Europe/Samara', 'Asia/Yekaterinburg', 'Asia/Omsk',
    'Asia/Novosibirsk', 'Asia/Krasnoyarsk', 'Asia/Irkutsk', 'Asia/Yakutsk', 'Asia/Vladivostok', 'Asia/Magadan',
    'Asia/Kamchatka', 'Europe/Minsk', 'Europe/Kiev', 'Asia/Almaty', 'Asia/Tbilisi', 'Asia/Yerevan', 'Europe/Berlin',
    'Europe/London', 'UTC'];
}

/** 'Europe/Moscow' → 'Москва (UTC+3)' where the browser can name it, else 'Europe/Moscow (UTC+3)'. */
export function fmtTimeZone(tz, ms = Date.now()) {
  let off;
  try { off = tzOffsetMin(ms, tz); } catch { return tz; }
  const sign = off < 0 ? '−' : '+';
  const a = Math.abs(off);
  const offText = `UTC${sign}${Math.floor(a / 60)}${a % 60 ? ':' + pad2(a % 60) : ''}`;
  const city = tz.split('/').pop().replace(/_/g, ' ');
  return `${CITY_RU[tz] ?? city} (${offText})`;
}

const CITY_RU = {
  'Europe/Moscow': 'Москва', 'Europe/Kaliningrad': 'Калининград', 'Europe/Samara': 'Самара',
  'Asia/Yekaterinburg': 'Екатеринбург', 'Asia/Omsk': 'Омск', 'Asia/Novosibirsk': 'Новосибирск',
  'Asia/Krasnoyarsk': 'Красноярск', 'Asia/Irkutsk': 'Иркутск', 'Asia/Yakutsk': 'Якутск',
  'Asia/Vladivostok': 'Владивосток', 'Asia/Magadan': 'Магадан', 'Asia/Kamchatka': 'Камчатка',
  'Europe/Volgograd': 'Волгоград', 'Europe/Saratov': 'Саратов', 'Europe/Ulyanovsk': 'Ульяновск',
  'Europe/Astrakhan': 'Астрахань', 'Asia/Barnaul': 'Барнаул', 'Asia/Tomsk': 'Томск', 'Asia/Chita': 'Чита',
  'Asia/Sakhalin': 'Сахалин', 'Europe/Minsk': 'Минск', 'Europe/Kiev': 'Киев', 'Europe/Kyiv': 'Киев',
  'Asia/Almaty': 'Алматы', 'Asia/Tashkent': 'Ташкент', 'Asia/Tbilisi': 'Тбилиси', 'Asia/Yerevan': 'Ереван',
  'Asia/Baku': 'Баку', 'Europe/Berlin': 'Берлин', 'Europe/London': 'Лондон', 'Europe/Riga': 'Рига',
  'Europe/Helsinki': 'Хельсинки', 'Europe/Istanbul': 'Стамбул', 'Asia/Dubai': 'Дубай', 'Asia/Bangkok': 'Бангкок',
  'Europe/Belgrade': 'Белград', 'Europe/Warsaw': 'Варшава', 'Europe/Prague': 'Прага', 'America/New_York': 'Нью-Йорк',
};

/** Round minutes to a step (rounding setting). */
/**
 * Parts of the day, in the user's local time — the same fixed bounds the server uses for classification rules
 * (docs/web-api.md, "Activities"): a rule applies to an entry that starts inside its part of the day.
 */
export const PARTS_OF_DAY = [
  { part: 'morning', startMin: 360, endMin: 720, label: 'Утром', hours: '06–12' },
  { part: 'afternoon', startMin: 720, endMin: 1080, label: 'Днём', hours: '12–18' },
  { part: 'evening', startMin: 1080, endMin: 1440, label: 'Вечером', hours: '18–24' },
  { part: 'night', startMin: 0, endMin: 360, label: 'Ночью', hours: '00–06' },
];
/** Part of the day a minute of a local day falls into (minutes outside 0…1439 wrap around). */
export function partOfDay(min) {
  const m = ((Math.floor(min) % MINUTES_PER_DAY) + MINUTES_PER_DAY) % MINUTES_PER_DAY;
  return PARTS_OF_DAY.find((x) => m >= x.startMin && m < x.endMin).part;
}

export const roundTo = (min, step = 5) => (step > 1 ? Math.round(min / step) * step : Math.round(min));
export const floorTo = (min, step = 5) => (step > 1 ? Math.floor(min / step) * step : Math.floor(min));
