// Design C «Нить» — the thread: the day as one line of light, text to its right, nothing else.
// Rendering only: geometry comes from core/timeline.js (layoutDay), taps go to core flows.
import { layoutDay, describePiece, initialScrollTop } from '/core/timeline.js';
import { h, reconcile, setText, setData } from '/core/dom.js';
import * as T from '/core/time.js';
import { timelineDrag } from '/core/drag.js';

export const HOUR = 72;                 // px per hour
export const HEAD_EXT = 34;             // the «Сегодня» band under the header on other days (px)
const LAYOUT = { hourHeight: HOUR, minBlockHeight: 22, shortHeight: 48, minHit: 44 };
const LABEL_TWO = 42;                   // two-line label height incl. its 2 px offset
const LABEL_ONE = 22;
const STICKY_MIN = 150;                 // pieces at least this tall keep their label in view while scrolling
const NOTE_H = 16;
const STICKY_TOP = 26;                  // a stuck label sits this far below the header (matches style.css)

export function createThread(app, flows, { scroller, track, head, onOpened }) {
  const hoursEl = h('div', { class: 'c-hours', 'aria-hidden': 'true' });
  const wire = h('div', { class: 'c-wire', 'aria-hidden': 'true', hidden: true });
  const piecesEl = h('ol', { class: 'c-pieces', 'aria-label': 'Лента дня' });
  const parEl = h('ol', { class: 'c-par', 'aria-label': 'Параллельные занятия' });
  const nowTime = h('span', { class: 'c-now-time' });
  const nowEl = h('div', { class: 'c-now', 'aria-hidden': 'true', hidden: true },
    nowTime, h('span', { class: 'c-now-line' }), h('span', { class: 'c-now-dot' }));
  const skel = h('div', { class: 'c-skel', 'aria-hidden': 'true' },
    ...[[8, 1.5], [9.75, 3.2], [13.2, 1.1], [14.5, 2.6]].map(([at, len]) =>
      h('div', { class: 'c-skel-piece', style: { top: `${at * HOUR}px`, height: `${len * HOUR}px` } }, h('i'), h('b'), h('s'))));
  const retry = h('button', { type: 'button', class: 'c-note-action', onClick: () => app.actions.refresh({ full: true }) }, 'Повторить');
  const noteText = h('span');
  const note = h('p', { class: 'c-thread-note', role: 'status', hidden: true }, noteText, retry);
  track.append(hoursEl, wire, skel, piecesEl, parEl, nowEl, note);

  let L = null;
  let lastDay = null;
  let lastMe = null;
  let shownDate = null;
  let hourCount = 0;
  let enterTimer = 0;
  let pendingFor = null;

  // ---------- model of one piece ----------
  function pieceModel(b, i, day, me) {
    const p = b.piece;
    const d = describePiece(p, me);
    const prev = L.blocks[i - 1]?.piece;
    const running = !!p.open && day.isToday;
    const cont = running && !!prev?.open && prev.segment?.id === p.segment?.id; // a later part of the running activity
    const free = d.future && d.unknown;
    let size = b.tiny || free ? 'none' : b.short ? 'one' : 'two';
    let title = d.title;
    let sub = `${d.durationText} · ${d.rangeText}`;
    let dur = d.durationText;
    if (running && !cont && day.current) {
      const from = p.segment?.startLocal ?? '';
      sub = `${T.fmtDuration(day.current.elapsedMin)}${from.slice(0, 10) === day.date ? ` · с ${from.slice(11, 16)}` : ''}`;
      dur = T.fmtDuration(day.current.elapsedMin);
    } else if (cont) {
      // The same activity going on: no second title. Past its norm it only asks quietly.
      if (d.kind === 'questionable' && !b.tiny) { size = 'one'; title = 'всё ещё?'; dur = ''; } else size = 'none';
    }
    const room = b.height - 8;
    const sticky = size === 'two' && b.height >= STICKY_MIN;
    const confirm = d.actions.confirm && size !== 'none';
    const mark = d.kind === 'questionable' && size !== 'none' && !d.future;
    const aria = `${d.title}, ${d.rangeText}, ${d.durationText}, ${d.statusLabel.toLowerCase()}${running ? ', идёт сейчас' : ''}`;
    const m = {
      b, index: b.index, title, sub, dur, size, sticky, confirm, mark, aria,
      kind: d.kind, group: d.group, cat: d.category.key ?? '', open: running, cont, free, unknown: d.unknown, future: d.future,
      top: b.hitY, height: b.hitHeight, segTop: b.y - b.hitY + 1, segH: Math.max(2, b.height - 2),
      railH: sticky ? room : 0, confirmLabel: `Подтвердить: ${d.title}`,
      fromPrev: p.startMin === 0 && !!p.segment?.startLocal && p.segment.startLocal.slice(0, 10) < day.date,
      toNext: p.endMin === day.dayMinutes && !d.future && (p.segment ? p.segment.endLocal == null || p.segment.endLocal.slice(0, 10) > day.date : false),
    };
    m.sig = [m.title, m.sub, m.dur, m.size, m.sticky, m.confirm, m.mark, m.kind, m.group, m.cat, m.open, m.cont, m.free,
      m.top, m.height, m.segTop, m.segH, m.railH, m.fromPrev, m.toNext].join('|');
    return m;
  }

  function createPiece() {
    const r = {
      hit: h('button', { type: 'button', class: 'c-hit' }),
      title: h('span', { class: 'c-title', 'aria-hidden': 'true' }),
      sub: h('span', { class: 'c-sub', 'aria-hidden': 'true' }),
      dur: h('span', { class: 'c-dur', 'aria-hidden': 'true' }),
      confirm: h('button', { type: 'button', class: 'c-confirm', hidden: true }),
      mark: h('span', { class: 'c-mark', 'aria-hidden': 'true', hidden: true }, '?'),
    };
    const el = h('li', { class: 'c-piece' }, r.hit, h('span', { class: 'c-seg', 'aria-hidden': 'true' }),
      h('div', { class: 'c-rail' }, h('div', { class: 'c-label' },
        h('span', { class: 'c-text' }, r.title, r.sub), r.dur, h('span', { class: 'c-slot' }, r.confirm, r.mark))));
    el.__r = r;
    return el;
  }

  function updatePiece(el, m) {
    el.__b = m.b;
    if (el.__sig === m.sig) return;
    el.__sig = m.sig;
    const r = el.__r;
    const st = el.style;
    st.top = `${m.top}px`;
    st.height = `${m.height}px`;
    st.setProperty('--st', `${m.segTop}px`);
    st.setProperty('--sh', `${m.segH}px`);
    st.setProperty('--rh', m.railH ? `${m.railH}px` : 'auto');
    setData(el, 'kind', m.kind);
    setData(el, 'group', m.group);
    setData(el, 'cat', m.cat || null);
    setData(el, 'size', m.size);
    setData(el, 'open', m.open ? 'true' : null);
    setData(el, 'cont', m.cont ? 'true' : null);
    setData(el, 'free', m.free ? 'true' : null);
    setData(el, 'unknown', m.unknown ? 'true' : null);
    setData(el, 'sticky', m.sticky ? 'true' : null);
    setData(el, 'fromPrev', m.fromPrev ? 'true' : null);
    setData(el, 'toNext', m.toNext ? 'true' : null);
    setText(r.title, m.title);
    setText(r.sub, m.sub);
    setText(r.dur, m.dur);
    r.confirm.hidden = !m.confirm;
    if (m.confirm) r.confirm.setAttribute('aria-label', m.confirmLabel);
    r.mark.hidden = !m.mark;
    r.hit.setAttribute('aria-label', m.aria);
    r.hit.disabled = m.future;
  }

  // ---------- parallel track: where the «+ Обед · 30 мин» note fits ----------
  function placeNotes(day, me) {
    let lastBottom = -Infinity;
    const items = L.parallel.map((b) => {
      const d = describePiece(b.piece, me, { parallel: true });
      const mi = L.blocks.findIndex((x) => b.y >= x.y && b.y < x.y + x.height);
      const main = L.blocks[mi];
      let noteY = null;
      if (main) {
        const mp = main.piece;
        const hasLabel = !(main.tiny || (mp.future && mp.source === 'background'));
        const labelH = !hasLabel ? 0 : main.short ? LABEL_ONE : LABEL_TWO;
        const y = Math.max(b.y + 1, main.y + labelH + 4, lastBottom + 2);
        if (y + NOTE_H <= main.y + main.height - 2 && y <= b.y + b.height + 8) {
          noteY = y;
          lastBottom = y + NOTE_H;
        }
      }
      const top = noteY == null ? b.hitY : Math.min(b.hitY, noteY - 14);
      const bottom = noteY == null ? b.hitY + b.hitHeight : Math.max(b.hitY + b.hitHeight, noteY + 30);
      const m = {
        b, d, noteY, host: mi, lane: Math.min(b.lane, 2), y: b.y + 1, height: Math.max(2, b.height - 2), hitTop: top, hitHeight: bottom - top,
        note: `+ ${d.title} · ${d.durationText}`, aria: `Параллельно: ${d.title}, ${d.rangeText}, ${d.durationText}`,
      };
      m.sig = [m.noteY, m.lane, m.y, m.height, m.hitTop, m.hitHeight, m.note, d.group, d.kind].join('|');
      return m;
    });
    return items;
  }

  function updateParallel(el, m) {
    el.__b = m.b;
    el.__m = m;
    if (el.__sig === m.sig) return;
    el.__sig = m.sig;
    const [hit, seg, noteEl] = el.children;
    setData(el, 'group', m.d.group);
    setData(el, 'kind', m.d.kind);
    setData(el, 'note', m.noteY == null ? null : 'true');
    el.style.setProperty('--lane', String(m.lane));
    seg.style.top = `${m.y}px`;
    seg.style.height = `${m.height}px`;
    hit.style.top = `${m.hitTop}px`;
    hit.style.height = `${m.hitHeight}px`;
    hit.setAttribute('aria-label', m.aria);
    noteEl.hidden = m.noteY == null;
    if (m.noteY != null) noteEl.style.top = `${m.noteY}px`;
    setText(noteEl, m.note);
  }

  // ---------- render ----------
  function render(s) {
    if (s.phase !== 'ready') return;
    const day = s.day && s.day.date === s.viewDate ? s.day : null;
    if (!day) { renderPending(s); return; }
    if (day === lastDay && s.me === lastMe) return;
    lastDay = day;
    lastMe = s.me;
    L = layoutDay(day, LAYOUT);
    setData(track, 'state', 'ready');
    note.hidden = true;
    track.style.height = `${L.height}px`;

    if (hourCount !== L.hours.length) {
      hourCount = L.hours.length;
      hoursEl.replaceChildren(...L.hours.map((x) => h('span', { class: 'c-hour', style: { top: `${x.y}px` } }, T.pad2(x.hour % 24))));
    }
    const hourEls = hoursEl.children;
    for (let i = 0; i < hourEls.length; i++) hourEls[i].classList.toggle('c-hour--off', L.nowY != null && Math.abs(L.hours[i].y - L.nowY) < 14);

    const items = placeNotes(day, s.me);
    const models = L.blocks.map((b, i) => pieceModel(b, i, day, s.me));
    reconcile(piecesEl, models, { key: (m) => m.b.key, create: createPiece, update: updatePiece });
    reconcile(parEl, items, {
      key: (m) => m.b.key,
      create: () => h('li', { class: 'c-par-piece' }, h('button', { type: 'button', class: 'c-par-hit' }),
        h('span', { class: 'c-par-seg', 'aria-hidden': 'true' }), h('span', { class: 'c-par-note', 'aria-hidden': 'true' })),
      update: updateParallel,
    });
    // Notes inside a piece whose label stays in view: they step aside when the label reaches them.
    watched = [...parEl.children].filter((el) => el.__m?.noteY != null && models[el.__m.host]?.sticky).map((el) => ({ el: el.lastElementChild, y: el.__m.noteY }));
    syncNotes();

    // "Now": the accent dot rides the thread; past it the thread thins to a wire.
    const today = L.nowY != null;
    nowEl.hidden = !today;
    if (today) {
      nowEl.style.transform = `translate3d(0, ${L.nowY}px, 0)`;
      setText(nowTime, T.fmtClock(day.nowMin));
    }
    const allFuture = !today && day.pieces.length > 0 && day.pieces.every((p) => p.future);
    wire.hidden = !(today || allFuture);
    if (!wire.hidden) wire.style.top = `${today ? L.nowY : 0}px`;

    if (shownDate !== day.date) opened(day);
  }

  // A day that is not here yet (or failed to load): a calm placeholder thread, no spinner.
  function renderPending(s) {
    setData(track, 'state', s.dayError ? 'error' : 'loading');
    note.hidden = !s.dayError;
    if (s.dayError) setText(noteText, s.online === false ? 'Нет сети — день не загрузился.' : 'Не получилось загрузить день.');
    if (pendingFor === s.viewDate) return;
    pendingFor = s.viewDate;
    lastDay = null;
    shownDate = null;
    track.style.height = `${24 * HOUR}px`;
    reconcile(piecesEl, [], { key: () => '' });
    reconcile(parEl, [], { key: () => '' });
    nowEl.hidden = true;
    wire.hidden = true;
    scroller.scrollTop = Math.max(0, track.offsetTop + 7.5 * HOUR - head.offsetHeight - HEAD_EXT - 24);
    onOpened?.(null, { first: !track.dataset.seen });
  }

  // Once per opened day: scroll so that "now" sits at 40 % of the viewport (other days: the morning), then let the
  // thread grow from the first visible piece down.
  function opened(day) {
    const first = !track.dataset.seen;
    shownDate = day.date;
    pendingFor = null;
    track.dataset.seen = '1';
    const view = scroller.clientHeight;
    pad = track.offsetTop;
    const headH = head.offsetHeight + (day.isToday ? 0 : HEAD_EXT);
    scroller.scrollTop = day.nowMin != null
      ? initialScrollTop(L, view, { day, offsetTop: pad, offsetBottom: 4000 })
      : initialScrollTop(L, view, { day, offsetTop: pad - headH, offsetBottom: 4000 });

    const edge = scroller.scrollTop - pad + headH - 60;
    let n = 0;
    for (const el of piecesEl.children) {
      const b = el.__b;
      const visible = b && b.y + b.height > edge;
      el.style.setProperty('--d', `${visible && !b.piece.future ? Math.min(n++, 14) * 22 : visible ? Math.min(n, 14) * 22 : 0}ms`);
    }
    clearTimeout(enterTimer);
    delete track.dataset.enter;
    void track.offsetWidth;
    track.dataset.enter = 'true';
    enterTimer = setTimeout(() => { delete track.dataset.enter; }, 1000);
    syncNotes();
    onOpened?.(day, { first });
  }

  // ---------- parallel notes under a sticky label (opacity only, one rAF per scroll frame) ----------
  let watched = [];
  let pad = 0;
  let raf = 0;
  function syncNotes() {
    raf = 0;
    if (!watched.length) return;
    const ext = track.closest('[data-today="false"]') ? HEAD_EXT : 0;
    const stuck = scroller.scrollTop - pad + head.offsetHeight + ext + STICKY_TOP; // track y of a stuck label's top
    for (const n of watched) n.el.classList.toggle('c-par-note--under', stuck + LABEL_TWO + 2 > n.y);
  }
  scroller.addEventListener('scroll', () => { if (watched.length && !raf) raf = requestAnimationFrame(syncNotes); }, { passive: true });

  // ---------- taps ----------
  track.addEventListener('click', (e) => {
    const s = app.state;
    if (!s.day) return;
    const c = e.target.closest('.c-confirm');
    if (c) {
      const p = c.closest('.c-piece')?.__b?.piece;
      if (!p || s.busy) return;
      const end = Math.min(p.endMin, s.day.nowMin ?? p.endMin);
      app.actions.confirm({ start: T.minToLocal(s.day.date, p.startMin), end: T.minToLocal(s.day.date, end) });
      return;
    }
    const hit = e.target.closest('.c-hit, .c-par-hit');
    const b = hit?.parentElement.__b;
    if (b) flows.openPiece(b.piece, { parallel: hit.classList.contains('c-par-hit') });
  });

  // Long press: handles on a stored entry's edges, or a new entry dragged out over unrecorded time (core/drag.js).
  timelineDrag(app, flows, {
    scroller, surface: track, layout: () => L, ignore: '.c-confirm, .c-par',
    insets: () => ({ top: head.offsetHeight, bottom: document.getElementById('island')?.offsetHeight ?? 0 }),
  });

  return { render };
}
