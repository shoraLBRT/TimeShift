# TimeShift core — the interface for design authors

The core holds **all logic and behaviour** of the PWA: data, clock, refresh, optimistic changes, sheets, gestures,
toasts, every panel (flow) with semantic markup. A design (`/a/`, `/b/`, `/c/`) brings only what you see: its CSS,
its timeline / header / now-control rendering and icons. No build step, no dependencies, no other origins: plain ES
modules served from `/core/…`.

`core/dev.html` is the reference example (≈200 lines): read it first, then this file.

## 1. What a design must provide

```
/<x>/index.html     the page (see the skeleton below)
/<x>/*.css, *.js    your styles and rendering
```

- `<script src="/core/boot.js"></script>` in `<head>` before the CSS — applies the saved theme before the first paint.
- `<link rel="stylesheet" href="/core/base.css">` then your CSS.
- `<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">`, `theme-color`
  (both schemes), `apple-mobile-web-app-capable`, `<link rel="manifest" href="/manifest.webmanifest">`,
  `<link rel="apple-touch-icon" href="/icons/icon-180.png">`, `<link rel="icon" href="/icons/favicon.svg">`.
- A gate container for loading / login / onboarding (`flows.renderGate`).
- The main screen: header with the date, the timeline (inside your own scroll container), the "now" control, entry
  points for add / phrase / summary / settings. Icons are yours (the core ships none).
- Light and dark: style `[data-theme="light"]` and `[data-theme="dark"]` on `<html>`; the core always sets the
  resolved value (it follows `prefers-color-scheme` while the user's choice is «Как в системе»).
- Never rely on hover; tap targets ≥ 44 px; animate `transform` / `opacity` only.

Skeleton:

```html
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <script src="/core/boot.js"></script>
  <link rel="stylesheet" href="/core/base.css">
  <link rel="stylesheet" href="/x/style.css">
</head>
<body>
  <div id="gate"></div>
  <main id="main" hidden>…</main>
  <script type="module" src="/x/main.js"></script>
</body>
</html>
```

```js
import { createApp } from '/core/app.js';
import { createFlows } from '/core/flows.js';
const app = createApp({ design: 'x' });
const flows = createFlows(app, { sheet: { className: 'x-sheet' }, sheets: { switcher: { className: 'x-sheet--compact' } } });
flows.renderGate(document.getElementById('gate'));
app.subscribe((s, prev) => { document.getElementById('main').hidden = s.phase !== 'ready'; render(s, prev); });
app.start();
```

## 2. `createApp({ design, api? })` → `app`

| Member | |
|---|---|
| `app.state` | the current immutable (frozen) snapshot — see below |
| `app.subscribe(fn, { immediate = true })` | `fn(state, prev)` right away and after every change (batched per microtask). Returns `unsubscribe`. Compare `state.x !== prev.x` to skip work — every changed field is a new object. |
| `app.start()` | boots: snapshot from `localStorage` (instant start), `/api/meta`, user, today; timers and listeners |
| `app.actions.*` | everything a user can do (below) |
| `app.nowMs()` | server-corrected clock (ms) — use it instead of `Date.now()` |
| `app.api` | the client (live or demo) — normally not needed |

Demo: `?demo=1` (remembered for the tab; `?demo=0` leaves it), `&fresh=1` starts at onboarding, `&login=1` at the
login screen (any key works). `&at=HH:mm` sets the demo clock: the demo backend and the app clock (`nowMs()`,
`nowMin`, `today`) behave as if the local time were that time today, and keep ticking from there (remembered with
the demo flag for the tab; `?demo=1` without `at` returns to the real time; `state.demoAt` shows it). The chooser's
«Демо» links and previews use `&at=15:40` so the day is full. `?embed=1` marks an embedded frame (the chooser's side-by-side view): the demo flag is
not remembered and sheets do not touch history.

### State

```js
{
  phase: 'loading' | 'login' | 'onboarding' | 'ready',
  bootError: ApiError | null,        // first load failed (loading screen shows retry)
  loginError: string | null,
  demo: boolean, demoAt: 'HH:mm' | null, embed: boolean, design: string | null,
  online: boolean,                   // false after a network failure / 'offline' event → show a quiet «Нет сети»
  meta: Meta | null,                 // GET /api/meta
  me: Me | null,                     // GET /api/me (settings, sleep, blocks, categories, timeZone, quietUntilUtc)
  today: 'YYYY-MM-DD',               // in the user's zone; rolls over at local midnight
  viewDate: 'YYYY-MM-DD',            // the day on screen
  day: Day | null,                   // the viewed day, projected to "now" every 15 s (see below)
  dayLoading: boolean,               // no cached copy of viewDate yet
  dayError: ApiError | null,
  nowMin: number | null,             // minutes since the start of viewDate, or null when it is not today
  activities: Activity[],            // most recently used first
  busy: null | 'start' | 'stop' | 'continue' | 'addEntry' | 'updateEntry' | 'deleteEntry' | 'confirm' | 'undo' | 'quiet' | 'login' | 'onboarding' | 'push',
  thread: [{ id, from: 'user' | 'bot', text, buttons?: [[{ label, data }]], pending?, error?, pressed?, hasImage? }],
  threadBusy: boolean,
  summary: { from, to, data } | null, // last loadSummary result
  ping: { requested: boolean, pending: boolean }, // requested: open the switcher now; pending: server waits for an answer
  push: { supported, ios, standalone, permission, subscribed, needsInstall },
  install: { kind: 'installed' | 'ios' | 'prompt' | 'none', standalone, ios, text },
  theme: 'auto' | 'light' | 'dark', themeResolved: 'light' | 'dark',
  reloadPending: boolean,            // new assets cached; the core reloads at a safe moment
}
```

`Day`, `Me`, `Activity`, `Meta` are exactly the shapes of `docs/web-api.md`. `state.day` is the server's day moved
forward to the current minute between refreshes: the piece that ended at "now" grows, future pieces shrink,
`current.elapsedMin` and the totals follow. Mutations patch it optimistically; the server's answer replaces it.

### Actions

All return promises. Timeline mutations resolve to `{ ok, result?, error? }`, never throw, raise an undo toast
(«Отменить», 6 s) on success and an error toast with the server's Russian message on failure. While one runs,
`state.busy` is set and further mutations are refused (double-submit guard) — disable buttons on `busy`.

| Action | |
|---|---|
| `login(key)`, `logout()`, `retry()` | phases |
| `completeOnboarding()` | `POST /api/onboarding/complete`, then the app becomes `ready` |
| `goToDay(date)`, `prevDay()`, `nextDay()`, `goToday()` | instant from the in-memory cache (±1 day is prefetched), then revalidated |
| `refresh({ full })` | also runs on visible / focus / online and every 60 s while visible |
| `start({ activityId } \| { title }, at?)` | `at` — local time string; the running activity is closed |
| `stop({ at? })`, `continueCurrent()` | |
| `addEntry({ start, end, title \| activityId, category?, parallel? }, { inlineErrors? })` | local time strings |
| `updateEntry(segmentId, patch, { inlineErrors?, range?, title?, category?, parallel? })` | patch: any of `start end title category` |
| `deleteEntry(segmentId)` | |
| `confirm({ start, end, includeQuestionable? })`, `confirmDay()` | confirmDay = every assumed past piece of the viewed day |
| `undo()` | toast «Отменено» / «Нечего отменять» / «Уже нельзя отменить» |
| `quiet(minutes)` | `0` turns quiet hours off |
| `sendMessage(text)`, `pressButton(data, messageId)`, `clearThread()` | the assistant thread; a reply with `replacesSource` replaces the message whose button was pressed |
| `saveSettings(patch)`, `setSleep(sleep)`, `addBlock(b)`, `updateBlock(id, b)`, `deleteBlock(id)` | optimistic, rolled back on error, run one after another |
| `updateActivity(id, { name?, category?, typicalMin? })` | optimistic |
| `loadSummary({ from?, to? })` | resolves to the raw `/api/summary` data (default: 7 days ending with `viewDate`) |
| `enablePush()` | **call directly from a tap handler** (the permission prompt needs the gesture) |
| `disablePush()`, `testPush()`, `promptInstall()` | |
| `setTheme('auto' \| 'light' \| 'dark')` | persisted (`localStorage['ts.theme']`), applied as `data-theme` |
| `chooseDesign(x)`, `clearDesign()` | `clearDesign` goes to the chooser (`/?choose`) |
| `requestPing()`, `clearPing()` | flows open the switcher on `ping.requested` |

## 3. Timeline layout — `core/timeline.js`

```js
import { layoutDay, describePiece, initialScrollTop, blockAt } from '/core/timeline.js';
const L = layoutDay(state.day, { hourHeight: 64, minBlockHeight: 18, shortHeight: 40, minHit: 44 });
// L.height, L.pxPerMin, L.dayMinutes (1380 / 1500 on clock-change days), L.yOf(min), L.minOf(y), L.nowY (null when not today)
// L.hours:    [{ hour, min, label: '09:00', y }]
// L.blocks:   main track [{ key, index, piece, kind, startMin, endMin, y, height, short, tiny, hitY, hitHeight }]
// L.parallel: parallel track, same fields + lane (0…L.lanes-1)
scroller.scrollTop = initialScrollTop(L, scroller.clientHeight, { day: state.day, offsetTop: headerHeightInsideScroller });
```

- `key` is stable between renders (segment id, or source + start for computed pieces) — use it for keyed DOM
  (`reconcile` in `dom.js`) so pieces animate instead of being rebuilt.
- `tiny` — too small for any label; `short` — one line only. Render tiny pieces as a thin bar and put a transparent
  hit area at `hitY` / `hitHeight` (≥ 44 px, neighbours split the contested space). `blockAt(L.blocks, y)` finds the
  piece under a tap.
- `kind`: `confirmed | assumed | questionable | future`.
- `initialScrollTop` puts "now" at 40 % of the viewport (other days: the morning). Call it once per opened day, not on
  every render. `scrollTopFor(L, min, viewport, { ratio })` for any minute.

`describePiece(piece, me, { parallel })` → display model:

```js
{ title,           // background + questionable → «Не записано» (never «Залипание»); future background → «Свободно»
  unknown,         // true for background (unknown time)
  kind, status, source, future, open, parallel,
  category: { key, name, group, parent, parentName }, group,  // group 'none' when unclassified
  startMin, endMin, durationMin, rangeText: '09:00–10:30', durationText: '1 ч 30 мин', startText, endText ('сейчас' when open),
  statusLabel: 'Подтверждено' | 'Предположено' | 'Под вопросом' | 'План', sourceLabel,
  actions: { confirm, continue, fill, edit, delete }, segmentId, activityId }
```

Other helpers: `GROUPS`, `groupName(key)`, `categoryInfo(key, me)`, `categoriesByGroup(me)`, `pieceKind`, `pieceKey`,
`questionablePieces(day)`, `assumedPieces(day)`, `pieceBounds(piece, day)`, `daySummary(day, me)`,
`rangeSummary(summary, me)`, `summarizeTotals`, `summarizeStatus`, `advanceDay`, `patchDay`.

Category groups: `useful` Полезное, `necessary` Необходимое, `people` Люди, `rest` Отдых, `lost` Потерянное,
`none` Без категории. Colour pieces by `category.group` (and optionally by `category.key`).

## 4. Time — `core/time.js`

`fmtClock(545) → '09:05'`, `fmtDuration(150) → '2 ч 30 мин'`, `fmtCompact(80) → '1:20'`, `fmtRange(a, b)`,
`fmtDateLabel(date, today) → 'Сегодня' | 'Вчера' | 'Завтра' | 'Вторник, 17 ноября'`, `fmtDateLong`, `fmtDateShort`
(`'вт, 17 ноя'`), `fmtDateChip`, `fmtDayMonth`, `fmtMonthYear`, `fmtDateSpan`, `plural(n, one, few, many)`;
`MONTHS`, `MONTHS_GEN`, `MONTHS_SHORT`, `WEEKDAYS`, `WEEKDAYS_SHORT` (index 0 = Monday);
`addDays`, `diffDays`, `weekday` (ISO 1…7), `weekStart`, `weekDates`, `lastDays`, `isWeekend`;
`minToLocal(date, min)`, `localToMin(local, date)`, `parseLocal`, `parseClock`; zones: `todayIn(tz, ms)`,
`nowMinIn(tz, ms)`, `utcToLocal(ms, tz)`, `localToUtc(local, tz)`, `tzOffsetMin`, `deviceTimeZone()`,
`timeZones()`, `fmtTimeZone(tz)`; `roundTo`, `floorTo`.

## 5. Flows — `core/flows.js`

`createFlows(app, { sheet, sheets, autoPing = true, onClose })`. `sheet` — options for every sheet (usually a
`className`); `sheets.<flow>` — per-flow overrides (`detent`, `className`). Each flow sheet also gets the class
`ts-sheet--<flow>`. Defaults: `switcher`, `entry`, `gap` — `detent: 'auto'`; `summary`, `assistant`, `settings` —
`'large'`. One sheet per flow; opening it again replaces it.

| | |
|---|---|
| `flows.openPiece(piece, { parallel })` | running piece → switcher; questionable → gap; otherwise entry. Future pieces: nothing. |
| `flows.openSwitcher({ ping })` | the now control and push pings |
| `flows.openAdd(draft?)` | new past entry; draft `{ startMin, endMin, title?, category?, parallel? }` on the viewed day |
| `flows.openEntry(piece)`, `flows.openGap(piece)` | direct |
| `flows.openAssistant(text?)` | text is sent right away |
| `flows.openSummary()` | |
| `flows.openSettings(page?)` | `'schedule' \| 'reminders' \| 'tracking' \| 'activities' \| 'other'` |
| `flows.closeAll()` | |
| `flows.renderGate(container)` | loading / login / onboarding into `container`; sets `data-phase`, hides it when ready |

Pings: `?from=ping` (push `url`), a notification tap while the app is open (service-worker message) or
`app.actions.requestPing()` open the switcher with the ping question. `state.ping.pending` — the server sent a ping
nobody answered: highlight your now control.

## 6. Sheet — `core/sheet.js`

```js
const s = openSheet({ content: nodeOrPanel, title, className, detent: 'auto' | 'large', dismissible: true, onClose(reason) {}, initialFocus, footer, label });
s.el; s.body; s.header; s.close(reason); s.setTitle(text); await s.closed; s.isOpen();
configureSheets({ closeContent: () => myIconNode, closeLabel: 'Закрыть', history: true });
openSheetCount(); topSheet(); closeAllSheets();
```

Markup:

```
div.ts-sheet-backdrop[data-state=opening|open|closing][data-depth]
section.ts-sheet.<className>.ts-sheet--<flow>[role=dialog][aria-modal][data-state][data-detent=auto|large][data-dragging][data-depth][data-stacked]
  div.ts-sheet-grabber
  header.ts-sheet-header[data-empty]  > h2.ts-sheet-title, button.ts-sheet-close[data-ts-close] (empty: put your icon via configureSheets or ::before)
  div.ts-sheet-body                    the scroll area (the panel lives here)
  div.ts-sheet-footer                  only with `footer`
```

Behaviour you get: transform-only open/close; finger-following drag from the grabber/header and from the body when it
is scrolled to the top; rubber band above the top; velocity-aware dismissal (the close animation continues the
finger's speed); backdrop tap, Escape, the back button / gesture (a history entry per sheet) close the top sheet;
focus moves in and is trapped, returns on close; the page behind is `inert` and does not scroll; stacked sheets
(`data-depth` 0 = top, `data-stacked="true"` for the ones below — e.g. scale them back); the on-screen keyboard never
covers the focused field (the sheet sits above it: `--ts-keyboard-inset`, `--ts-vvh`, `html.ts-keyboard-open`);
`prefers-reduced-motion`; safe areas. Any element with `data-ts-close` inside a sheet closes it; `data-ts-nodrag`
excludes an area from drag. Elements with `data-ts-overlay` on `<body>` stay interactive while a sheet is open
(the toast host has it).

CSS variables (set on `:root`): `--ts-sheet-duration` (420ms), `--ts-sheet-easing`, `--ts-sheet-backdrop-duration`,
`--ts-sheet-top-gap`, `--ts-sheet-max-width` (640px, centred on wide screens), `--ts-sheet-z`. Style the sheet
background, radius, shadow, grabber look, header, backdrop colour yourself.

## 7. Toast — `core/toast.js`

`showToast({ message, action?: { label, onClick }, kind: 'info' | 'error' | 'success', duration = 6000 })` →
`{ el, close() }`; `hideToast()`. One at a time; swipe down or sideways dismisses; touching pauses the timer.
Markup: `div.ts-toast-host[role=status]` > `div.ts-toast[data-kind][data-state=entering|shown|leaving][data-dragging]`
> `span.ts-toast-message` + `button.ts-toast-action`. Variables: `--ts-toast-duration`, `--ts-toast-easing`,
`--ts-toast-bottom` (raise it above your bottom bar), `--ts-toast-z`.

## 8. Gestures — `core/gestures.js`

```js
swipeHorizontal(scroller, {
  onStart() {}, onProgress(dx, { width, progress }) { page.style.transform = `translateX(${dx}px)`; },
  onEnd({ direction /* 1 = previous day, -1 = next day, 0 = snap back */, dx, velocity, width }) {},
  threshold: 0.22, velocity: 0.35, edge: 16, canSwipe: (dir) => true, mouse: false,
}) → { destroy() }
longPress(el, (e, target) => {}, { ms: 450, tolerance: 10, selector }) → { destroy() }
```

The swipe locks direction after 10 px and never fights vertical scrolling (`touch-action: pan-y` via
`.ts-swipe-area`); a swipe swallows the click that would follow. `canSwipe` returning false rubber-bands that
direction. Elements with `data-ts-noswipe` (or horizontally scrollable ones) are ignored.

## 9. DOM helpers — `core/dom.js`

`h(tag, props, ...children)` (props: `class`, `style` object incl. `--vars`, `dataset`, `onClick`…, boolean props,
attributes); `reconcile(parent, items, { key, create, update, remove(el, done) })` keeps one node per key (exit
animations via `remove`); `setText`, `setData`, `replaceChildren`, `uid`, `focusables`, `isTyping`,
`prefersReducedMotion`, `rafOnce`.

## 10. PWA — `core/pwa.js`, `/sw.js`

The app registers `/sw.js` itself. `installInfo()`, `promptInstall()`, `pushState()`, `enablePush(api, key)`,
`disablePush(api)`, `checkAssets(version)`, `onPwa(fn)`; `isIos`, `isStandalone()`. On `localhost` the worker is
network-first; elsewhere cache-first for same-origin GET outside `/api/`. When `meta.assetsVersion` changes the worker
rebuilds its cache from the network and the page reloads once when it is safe (hidden, or no sheet open, no typing,
idle 2.5 s). Notifications: tag replaces the previous one; buttons «Продолжаю» / «Закончил» call the API directly;
a tap focuses the app and opens the switcher.

## 11. Panels — markup and hooks to style

Every panel is `panel(app, opts) → { el, title, destroy() }` (flows create them; you rarely do). Shared controls:

| Class | Element / hooks |
|---|---|
| `.ts-button` | `button[data-variant=primary\|secondary\|danger][data-action=…]`, `:disabled` while busy |
| `.ts-chip` | `button[aria-pressed]`, `data-*` per use (`data-activity-id`, `data-category`, `data-group`, `data-minutes`, `data-day`, `data-preset`); `.ts-activity-chip` for activities |
| `.ts-chips` | chip row `[data-kind=duration\|interval\|quiet\|ago\|preset]` |
| `.ts-field` | `label` > `.ts-field-label`, control, `.ts-field-hint`, `.ts-field-error[role=alert]`; `[data-invalid=true]`, control `[aria-invalid]` |
| `.ts-input` | text, password, number (`.ts-number`), search (`.ts-search`), time (`.ts-time`, `.ts-time-start`, `.ts-time-end`), textarea (`.ts-compose-input`) |
| `.ts-switch` | `label` > `input.ts-switch-input[type=checkbox][role=switch]` + `.ts-switch-label` + `.ts-switch-hint` |
| `.ts-options` | `[role=radiogroup]` > `label.ts-option[data-value]` > `input.ts-option-input[type=radio]` + `.ts-option-label` + `.ts-option-hint` (style `:has(:checked)`) |
| `.ts-weekdays` | 7 chips `[data-day=1…7][aria-pressed]` |
| `.ts-category-picker` | `details` > `summary.ts-category-summary` (`.ts-category-caption`, `.ts-category-value`) + `.ts-category-groups` > `.ts-category-group[data-group]` (`.ts-category-group-name`, `.ts-chips`); picker `[data-group]` = chosen group; chips `[data-depth=1]` for subcategories |
| `.ts-combo` | wrapper of an input + `ul.ts-suggest[role=listbox]` > `li.ts-suggest-item[aria-selected]` > `button.ts-suggest-button` (position the list yourself) |
| `.ts-recent` | `ul` > `li.ts-recent-item` > activity chip |
| `.ts-range` | `[data-open][data-crosses-midnight]` > `.ts-range-row` > `label.ts-range-field[data-edge=start\|end]` (`.ts-range-prev-day`, `.ts-range-next-day` «+1 день», `.ts-range-now` «сейчас») + `output.ts-range-duration` + duration chips |
| `.ts-note` | `p[data-kind=info\|warning\|install\|success]` |
| `.ts-hint`, `.ts-empty`, `.ts-loading`, `.ts-link` | text bits; `.ts-link` is a text button |
| `.ts-section` | `section` > `h3/h4.ts-section-title` |

Panels:

- **Gate** (`renderGate`): container `[data-phase]` > `.ts-gate[data-phase]` > one of:
  - `.ts-loading-screen[data-offline]` > `.ts-loading-title`, `.ts-loading-text`, retry `.ts-button[data-action=retry]`.
  - `form.ts-login[data-state=idle|busy|error]` > `h1.ts-login-title`, `p.ts-login-text`, `.ts-field` (password,
    `autocomplete=current-password`), `.ts-button[data-action=login]`, `p.ts-login-status`.
  - `section.ts-onboarding[data-step=tz|sleep|blocks|reminders|done][data-busy]` > `header.ts-onboarding-header` >
    `.ts-progress[role=progressbar]` (`--progress` 0…1) + `ol.ts-steps > li.ts-step[data-step][data-done][aria-current=step]`;
    `.ts-onboarding-page[data-step]` > `h2.ts-onboarding-title`, `p.ts-onboarding-text`, step content
    (`.ts-tz-current`, `[data-action=change-tz]`, `.ts-tz-picker`; sleep fields, `.ts-sleep-weekend`; `.ts-block-form`,
    `.ts-blocks`; interval chips, `.ts-push`; `ul.ts-onboarding-summary`); `footer.ts-onboarding-nav` >
    `[data-action=back]`, `[data-action=next|finish]`.
- **Switcher** `section.ts-switcher[data-running][data-ping][data-busy]` > `p.ts-switcher-question`;
  `.ts-current[data-over-typical]` > `.ts-current-label`, `.ts-current-title`, `.ts-current-elapsed`,
  `.ts-current-since`, `.ts-current-actions` > `[data-action=continue]` (primary), `[data-action=stop]`;
  `.ts-link[data-action=stop-earlier][aria-expanded]` → `.ts-stop-earlier` > ago chips, `.ts-stop-at` (time +
  `[data-action=stop-at]`); `h3.ts-switcher-subtitle`; `.ts-recent-holder` > `.ts-recent`; `form.ts-quick` >
  `.ts-combo` + `[data-action=start]`; `.ts-switcher-more` > `[data-action=add-past]`, `[data-action=assistant]`.
- **Entry** `form.ts-entry[data-mode=new|edit|computed][data-status][data-kind][data-parallel][data-dirty][data-busy]` >
  `.ts-entry-head` (`.ts-entry-status[data-kind]`, `.ts-entry-source[data-source]`), `.ts-combo` (title field +
  suggestions), `.ts-range`, `.ts-field-error`, `.ts-category-picker`, `.ts-switch` (new only: «Параллельно»),
  `.ts-entry-actions` > `[data-action=confirm]` «Всё верно» (assumed and unchanged), `[data-action=save]`,
  `[data-action=delete]`.
- **Gap** `section.ts-gap[data-source]` > `p.ts-gap-range` (`.ts-gap-time`, `.ts-gap-duration`), `p.ts-gap-question`,
  `.ts-recent` (one tap records the whole range), `form.ts-quick`, `.ts-gap-actions` > `[data-action=nothing]`
  «Ничего особенного», `[data-action=split]` «Только часть времени», `.ts-hint`.
- **Summary** `section.ts-summary` > `section.ts-summary-day` and `section.ts-summary-week[data-loading][aria-busy]`:
  `h3.ts-summary-title`, `.ts-summary-total`, `.ts-summary-span`, `.ts-status` > `.ts-status-bar[role=img]` >
  `span.ts-status-part[data-status][style=--share]` + `ul.ts-status-legend > li.ts-status-item[data-status]`
  (`.ts-status-label`, `.ts-status-value`); `h4.ts-summary-subtitle`; `ul.ts-totals[data-kind=group|category] >
  li.ts-total[data-group][data-category][style=--share]` (`.ts-total-name`, `.ts-total-value`, `.ts-total-share`,
  `.ts-bar`); `ol.ts-week > li.ts-week-day[data-date][data-weekday][aria-current=date] > button.ts-week-open`
  (`.ts-week-label`, `.ts-week-bar[style=--fill]` > `.ts-week-seg[data-group][style=--share]`, `.ts-week-total`);
  `.ts-summary-parallel`. `--share` / `--fill` are 0…1: e.g. `width: calc(var(--share) * 100%)` or `flex: var(--share)`.
  To draw your own chart, use the panel's `data` (`{ day: daySummary, range: rangeSummary }`) or the two functions.
  Unknown time: in the day model the questionable background minutes (`daySummary(...).unknownMin`, also
  `unknownMinutes(day)`) are taken out of `wasted` and shown as their own neutral row «Не записано»
  (`unknown: true`, group `none`, `li.ts-total[data-unknown=true]`); the questionable status part carries
  `unknownMin`. The range model keeps the server numbers (they cannot be separated) and labels them instead: the
  `wasted` row and the `lost` group get `includesUnknown: true` and a `label` («Залипание и не записанное»), the
  questionable part is labelled «Под вопросом (вкл. не записанное)», and `note` explains it (`p.ts-summary-note`).
  Render `label` (falls back to `name`); every row now has `label` and `unknown`.
- **Assistant** `section.ts-assistant[data-busy]` > `.ts-thread-empty` (`.ts-hint`, `ul.ts-examples > li >
  button.ts-example`), `ol.ts-thread[aria-live] > li.ts-msg[data-from=user|bot][data-pending][data-error]` >
  `p.ts-msg-text` (pre-wrap), `p.ts-msg-error`, `.ts-msg-buttons > .ts-msg-row > button.ts-msg-button[aria-pressed]`;
  `form.ts-compose` > `textarea.ts-compose-input` (grows to 160 px; Enter sends, Shift+Enter breaks) +
  `[data-action=send]`.
- **Settings** `.ts-settings[data-depth]` > `.ts-nav` > `section.ts-page[data-page][data-state=active|enter|below|hidden|leave]`
  (push / pop animate `transform`; tune `--ts-page-duration`, `--ts-page-easing`) > `header.ts-page-header`
  (sub-pages: `button.ts-page-back`, `h3.ts-page-title`) + `.ts-page-body`. Root: `ul.ts-menu > li >
  button.ts-menu-item[data-page]` (`.ts-menu-label`, `.ts-menu-value`). Pages: `schedule` (sleep fields,
  `.ts-sleep-weekend`, `.ts-blocks > ul.ts-block-list > li.ts-block-item[data-block-id][data-kind][data-category] >
  button.ts-block-open` (`.ts-block-title`, `.ts-block-meta`), `[data-action=add-block]`), `block`
  (`form.ts-block-form[data-mode=new|edit]`, `[data-action=add]`, `[data-action=delete]`), `reminders`
  (`.ts-reminders > .ts-section[data-section=push|interval|quiet|options]`: `.ts-push[data-state=unsupported|install|denied|off|on]`,
  `[data-action=test-push]`, `.ts-interval`, `.ts-quiet[data-active]` (`.ts-quiet-status`, `[data-action=quiet-off]`),
  `.ts-stepper-field > .ts-stepper` (`.ts-stepper-dec`, `output.ts-stepper-value`, `.ts-stepper-inc`)), `tracking`
  (`.ts-options` with `data-value=typical|default|infinite|custom`, `.ts-extension-custom`), `activities`
  (`ul.ts-activity-list > li.ts-activity-item[data-activity-id][data-group] > button.ts-activity-open`
  (`.ts-activity-name`, `.ts-activity-meta`)), `activity` (`.ts-activity-form`, `[data-action=clear-typical]`),
  `other` (`[data-action=change-design]`, `.ts-install[data-kind]`, `[data-action=install]`, `.ts-version`,
  `[data-action=logout]`), `tz` (`.ts-tz-picker > .ts-search + ul.ts-tz-list > li.ts-tz-item[aria-selected] >
  button.ts-tz-option[data-tz]` (`.ts-tz-label`, `.ts-tz-id`)). Every change saves at once; Escape inside a sub-page
  goes back instead of closing the sheet.

## 12. Checklist of states to style

- [ ] loading; loading failed (`.ts-loading-screen[data-offline=true]`, retry)
- [ ] login: idle, error, busy; demo hint
- [ ] onboarding: each of the 5 steps, progress, tz list, block list empty / filled, push states incl. iPhone install note
- [ ] empty day (fresh account: all background), not-today (no now marker, `nowMin === null`), future day (all `future`)
- [ ] `dayLoading` with no cached day; `online === false` («Нет сети», quiet); `busy` on buttons
- [ ] pieces: confirmed, assumed, questionable (background shows «Не записано»), future; `open` (the running
      activity, growing); `tiny` and `short` heights with their hit areas; over-norm extension (questionable + open)
- [ ] parallel track (lanes), a segment crossing midnight (starts at 0 / ends at `dayMinutes`)
- [ ] now control: running (title, elapsed, over typical), nothing running, `ping.pending`
- [ ] sheets: auto and large detents, dragging, stacked (`data-stacked`), with keyboard open
- [ ] toast: info with «Отменить», error
- [ ] every panel above, light and dark, `prefers-reduced-motion`, safe areas (notch, home indicator)

## 13. Known gaps

- Server totals count background time under `wasted`: the day summary separates it using the pieces, the 7-day
  summary can only label it (see Summary above).
- No offline queue: mutations made offline fail with a toast.
- Settings sub-pages are not history entries: the system back closes the whole settings sheet.
- `state.day` between refreshes is a projection; status changes caused by passing a norm appear on the next refresh
  (≤ 60 s).
- The demo's message engine understands only short activity names and «[вчера] 19-21 зал»-style lines.
