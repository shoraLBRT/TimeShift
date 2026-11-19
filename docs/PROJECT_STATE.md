# Project state

_Updated 2026-11-19._

## Stage

Two tracks, order in `docs/ROADMAP.md`:

- **MVP (Telegram bot)** — stage 9 (deploy); steps 1–8 of spec §11 are done.
- **PWA (own mobile client)** — built; all three designs stay and are switched in the app settings.

## What exists

- **Domain:** timeline layers (segments → open-activity extension ×norm/×max → week template → background),
  period resolution (§3.5), breaks inside an activity and parallel activities (§3.6), `Timeline.Apply/Stop/
  Continue/Undo`, ping rule `PingSchedule`.
- **Application:** client-neutral dialog `ConversationService` (onboarding, `/blocks`, `/settings`, `/now`,
  `/day [вчера|сб|13.11]` as text with ◀ ▶ and the parallel track, `/week` (◀ ▶ by week), `/add` (past entries without LLM), `/quiet`, `/undo`, `/facts`, `/categories`, short free text = new activity), `TrackingService`, `PingService`.
- **Infrastructure:** EF Core with SQLite (dev) and Postgres (prod), two migration sets; segments, activities,
  undo journal, schedule blocks.
- **Telegram:** long polling, inline keyboards, `TelegramNotifier` for pings; optional proxy. **Closed by default**
  (#49): only chats in `Telegram:AllowedChatIds` are served, others get «Этот бот личный» and their chat id in the
  log; `Telegram:AllowAnyone=true` opens it. A server without the list answers nobody until the owner adds their
  chat id (`docs/deploy.md`, `TELEGRAM_ALLOWED_CHAT_ID` in `.env`).
- **LLM in the dialog (`IntentApplier`, on when `Llm:ApiKey` is set):** free text → intents; periods are
  proposed with ✅/✏️ (⚪ after 10 min or on the next message), a missing route fact is asked once and remembered
  (`user_facts`), start/stop/quiet/facts/classify apply at once, parallel items go to the parallel track; every
  LLM call is logged in `messages`. `edit_segment` finds the segment by day / time inside / name and changes boundaries, name or category (`Timeline.Edit`).
- **LLM building blocks:** `ILlmProvider` + OpenAI-compatible adapter (`Llm:*`),
  intent contract (`Intents`, `IntentParser`), prompt with context (`IntentPrompt`), `IntentExtractor` with one
  retry, day/time resolution `PastTime`.
- **LLM eval (`tools/TimeShift.LlmEval`):** 44 phrases with expected intents, `IntentMatcher`, live run with
  accuracy/latency/cost and `--record`; offline checks in `dotnet test`. No live run yet (no key, #2).
- **Daily review (`DailyReview`):** evening — by the ping loop at «sleep − offset» (up to 30 min after sleep starts) or when sleep is started: the day, remaining 🟡 one by one, «⚪ Остальное верно» materializes ⚪ as ✅; morning — first message after the template sleep: «Спал как обычно?» (yes → ✅ sleep, no → «во сколько лёг и встал»). Once a day each; nothing before registration.
- **Yellow review (`YellowReview`):** after a text/voice message or an answer button, the first unreviewed 🟡 span (≥ 15 min, last 12 h, after onboarding) is asked «Пока тебя не было…» — recent activity / wasted / describe (`/add`) / skip; each span once (`users.yellow_reviewed_until`).
- **Classification (`Domain.Categories.Classifier`):** spec §4.2 defaults by name; YouTube, games and unknown activities get a one-time category question (buttons or a category name).
- **Questions (`QuestionService`, `pending_questions`):** category, then unknown typical duration of the current activity is asked once, right after a reply, one at a time; answers by button or a bare duration; `/facts` lists durations, categories, classification rules and routes.
- **Rendering (`TimeShift.Rendering`, SkiaSharp):** `/day` is a PNG (vertical day bar, colour = category, ✅ solid / ⚪ pale / 🟡 hatched, «now» line, parallel column) with the text and «Итого» per category as caption; `/week` is a PNG (a strip per day, a bar per category with hours and the difference with the previous week, one bar for the ✅/⚪/🟡 shares, parallel total) with the same numbers as caption; Docker image installs DejaVu for Cyrillic.
- **Week numbers (`WeekStats`):** calendar week Mon–Sun, the current one up to «now»; built from the same day windows as `/day`, so the week equals the sum of its days; days before registration are not counted; comparison with the same part of the previous week when that week is wholly after registration (`docs/decisions.md`).
- **Speech (`TimeShift.Speech`):** voice → OGG/Opus (Telegram, decoded in C# with Concentus) or WAV (the PWA; PCM 16 / float 32, any rate and channel count, recognised by signature) → PCM 16 kHz → local whisper.cpp (Whisper.net); `Speech:ModelPath`, `Speech:AutoDownload`; off without a model.
- **Web API (`TimeShift.Web`, contract `docs/web-api.md`):** single-owner access key with a stateless signed
  session cookie; day (pieces mapped to stored segments), summary, start / stop / continue, entries, confirm,
  undo, quiet, activities, settings, week template, free text and voice (`POST /api/messages` with an
  `audio/wav` body, the reply starts with what was heard) through the same dialog engine. Use cases live in
  `Application/Clients`. A user can exist without Telegram (`telegram_chat_id` is nullable). `day.current.category`
  is the category of the running entry itself, as in its piece and the totals (it no longer falls back to the
  activity's default).
- **Notifications:** channels fan-out (`NotificationFanout`): Telegram and Web Push (VAPID keys from config or
  generated once and kept in the database, subscriptions in `push_subscriptions`).
- **PWA (`src/TimeShift.Host/wwwroot`, no build step, no third-party requests):** shared core (`core/`: API
  client, in-memory demo backend `?demo=1&at=15:40`, state, timeline layout, bottom sheet, toasts, gestures,
  functional panels, push, service worker) and three complete design MVPs on top of it — `a/` «Лист» (paper
  planner), `b/` «Сетка» (iOS calendar grid), `c/` «Нить» (dark typographic thread). All three stay: the look is switched in Настройки → Прочее → Вид;
  `/` opens the stored one (the chooser on the first run).
  Ping interval is 5–240 minutes.
- **Voice in the PWA (#50, code complete, not yet tried on a phone):** a hold-to-talk microphone in the phrase
  composer («Записать фразой») — hold and speak, release to send, slide away to cancel. `core/voice.js` records
  with `MediaRecorder` and re-encodes on the device to WAV 16 kHz mono; `core/panels/mic.js` is the button, styled
  in each design. The button exists only when `/api/meta` says `speech: true` (a model on the server) and the
  browser can record. The demo has it and "hears" a fixed phrase; `?demo=1&speech=0` shows the composer without it.
- **Dragging on the timeline (#51, code complete, not yet tried with a finger):** `core/drag.js`, one call per
  design. Long press a stored entry → handles on its edges; dragging one moves that boundary in steps of the
  rounding setting, with the time above the finger, and saves it on release (`PATCH /api/entries/{id}` — the
  server displaces the neighbours, the toast's «Отменить» returns the previous bounds). Long press unrecorded
  time and drag → the entry sheet opens for that interval. Works in all three designs; no API change.
- **Own categories (#52, closed):** the standard set stays in code, the user's own categories live in the
  table `categories` and are part of the `User` aggregate (`User.AddCategory / UpdateCategory / RemoveCategory`,
  `CategorySet` = standard + own). An own category has a name, a group and optionally a parent (two levels), and
  a generated key `u.…` that never changes. It is offered wherever a category is chosen (the bot's category
  question, the LLM contract, the PWA pickers), validated in the REST API and counted in `/day` «Итого», `/week`,
  the pictures and `GET /api/summary`. Deleting archives it: old segments keep their name, parent and totals, the
  category leaves every choice, activities and template blocks that had it lose it; adding the same name again
  brings it back. Edited in the PWA (Настройки → Категории, all three designs) and in Telegram (`/categories`);
  REST: `POST / PATCH / DELETE /api/categories` (`docs/web-api.md`). Decisions: `docs/decisions.md`.
- **Classification rules by time of day (#52, last part; spec §4.2):** a rule says "this activity in this part
  of the day is this category" — «YouTube по вечерам — отдых». The parts are fixed, in the user's time zone:
  morning 06–12, day 12–18, evening 18–24, night 00–06 (`PartsOfDay`, the same hours «вечером» means in text); the
  part is taken by the start of the entry. A rule is a user fact (`user_facts`: key `rule:<activity>:<part>`,
  new column `value` = category key; `minutes` is nullable now). The category of a new entry: named for the
  entry → the rule → the activity's default → nothing (the bot asks). `TrackingService.CategoryAsync` applies it
  on every recording path (dialog, pings, `/add`, 🟡 review, LLM intents and periods, the PWA); stored entries are
  never re-classified. Created in Telegram by the button «Только вечером (18:00–24:00)» under a category answer
  (no LLM needed; the bot then asks again at another time of day) or by a phrase through the LLM (`classify` with
  `when`); listed and removed in `/facts`; in the PWA — on the activity page (Настройки → Занятия, all three
  designs), REST `PUT / DELETE /api/activities/{id}/rules/{part}`. Deleting an own category drops the rules that
  led to it. Decisions: `docs/decisions.md`, «Classification rules by time of day».
- **The category answer reaches the running entry (#62):** when the bot's category question is answered — a
  button or a name, «Только вечером», or a `classify` phrase through the LLM, alone or among other intents of the
  message (#67) — the running entry of that activity gets the category too, if it has none, so the first entry of a new activity is counted in `/day`,
  `/week` and the PWA summary under the answer and not in «Без категории». What is written is what
  `TrackingService.CategoryAsync` gives that entry (a rule only when the entry started in its part of the day).
  It is a timeline edit and its own `/undo` step: `/undo` takes the category off the entry, the answer (the
  activity's default or the rule) stays. An entry that already has a category, closed entries and other
  activities are not touched; the reply says when the entry was changed. «Начал гитару, гитара — это учёба» in
  one message: the entry it starts is counted under «Учёба»; with `start` first that is the start plus an edit
  (two `/undo` steps), with `classify` first the entry takes the category at its start (one step). Decisions:
  `docs/decisions.md`, «The category answer reaches the running entry».
- **Reads inside one message see what the message has recorded (#65):** a message is saved once, at its end, and
  the EF repositories used to answer reads from the database alone. Now timeline reads (`LoadAsync`,
  `FindOpenAsync`, `FindSegmentAsync`), an activity by id / in the list, and the question queue answer as the
  database would after the save — stored rows plus the unit of work's unsaved additions, removals and edits. So
  the question about a new activity comes in the reply to the message that started it, the next question comes
  with the answer to the previous one, and an LLM message with several timeline intents builds each on the one
  before (before the fix such a message saved overlapping entries and the timeline could not be loaded again).
  The journal time grows strictly per user, so `/undo` takes the changes of one message back one by one in
  reverse order. No schema change. Covered on a real database at repository level (`PersistenceTests`) and at
  dialog level (`tests/TimeShift.Web.Tests/DialogTests.cs`, the whole host with a scripted LLM). Decisions:
  `docs/decisions.md`, «Reads inside one unit of work».
- **Unknown time (#47):** the background is «Не записано» with no category (`TimelinePiece.IsUnknown`), not
  «Залипание». It is counted apart from the categories everywhere: `/day` text («Итого: …» plus a line «Не
  записано: N»), `/day` and `/week` pictures (neutral grey), `/week` and `WeekStats` (a last line «Не записано»,
  `CategoryTotal.IsUnknown`, with the change against last week), REST (`unknownMin` in the day, each summary day
  and the range; not in `totals`) and the PWA (`summarizeTotals(..., unknownMin)` — the last row «Не записано» in
  the categories and the groups, never in «Потерянное»; the old «Залипание и не записанное» labels and the note
  are gone). The bot's question «Пока тебя не было…» offers «Залипание» first; onboarding explains the new
  meaning. Spec §2, §3.2, §3.4, §5.1 item 3 and §5.4 edited. No schema change.
- **Ping settings (#48):** what happens after `max_unanswered` unanswered pings is a setting — back-off
  (default: the interval doubles, at most 4 h, the first answer restores it) or stop (as before); pings can be
  switched off; daily quiet hours «не пинговать с … до …» in local time (may cross midnight). Domain
  `PingSchedule` (`UnansweredPingsPolicy`, `QuietHours`), `user_settings` columns `after_unanswered_pings`,
  `pings_enabled`, `quiet_start`, `quiet_end` (migration `PingSettings` for both providers; existing users get
  back-off and pings on), REST `pingsEnabled` / `afterUnansweredPings` / `quietHours`, Telegram `/settings`
  (two toggles and «🤫 Тихие часы»), PWA «Напоминания». Spec §5.2, §6, §7 and the `/settings` line of the commands table edited; decisions in
  `docs/decisions.md`, «Ping settings as built».
- **Telegram /settings and /blocks (#53):** numbers change with «− value +» (unanswered pings, evening review),
  rounding is a ✓ choice of 1 / 5 / 10 / 15 min, toggles stay toggles. 🗑 in `/blocks` moves the block to a
  one-place trash (`schedule_blocks.trashed`, migration `TrashedBlocks` for both providers) and the redrawn menu
  offers «↩️ Вернуть»; a block typed again with the same title replaces the same-titled blocks it overlaps.
  Spec §7 and the `/blocks` line of the commands table edited; decisions in `docs/decisions.md`, «Telegram
  /settings and /blocks as built».
- **Deploy:** `docker compose --profile web` adds Caddy with automatic HTTPS; guide in `docs/deploy.md`.
- **Host:** `PingWorker` once a minute (when any notification channel exists), `/health` with version, static
  files, `/api`.
- **Repo:** habze standard (PR-title check, release-please, Renovate) — PR #1; CI build + tests on PRs.
  Version 0.1.0 is released; release-please keeps a Release PR for the next version open (the maintainer merges
  it).
- **Tests on both databases (#17):** every test that needs a database gets it from `tests/Shared/TestDatabase.cs`
  — a temporary SQLite file, or its own Postgres database when `TIMESHIFT_TEST_POSTGRES` is set. CI runs the
  suite twice, on SQLite and on a `postgres:17` service container (Postgres migrations applied from scratch for
  every test; a skipped Postgres-only test fails that run). `MigrationTests` also fail, without any database,
  when the model is ahead of either migration set.

## Next

Everything in the roadmap's numbered order now waits for the maintainer:

- [#18](https://github.com/shoraLBRT/TimeShift/issues/18) (deploy to a VPS) — the maintainer chooses and pays for
  the VPS. [#19](https://github.com/shoraLBRT/TimeShift/issues/19) (logs, DB backups, monitoring) depends on #18.
- [#2](https://github.com/shoraLBRT/TimeShift/issues/2) — the LLM aggregator key.
- Open only for a check on real devices: #16 — the `/week` picture in Telegram on an iPhone (any running bot will
  do); #50 — say a 10–30 s phrase on an iPhone and on Android against a server with a whisper model
  (`Speech:ModelPath` or `Speech:AutoDownload`; the microphone needs HTTPS, so the deployed server); #51 — long
  press and drag on the timeline with a finger, in each design, watching that it does not fight scrolling.

What a coding session can take without waiting (outside the numbered order, `docs/ROADMAP.md` «Also»):

- [#49](https://github.com/shoraLBRT/TimeShift/issues/49) — closing the bot by default: merged (PR #72).
  When #18 deploys, the owner's chat id goes into `TELEGRAM_ALLOWED_CHAT_ID`, or the bot answers nobody.
- [#53](https://github.com/shoraLBRT/TimeShift/issues/53) — Telegram settings and blocks UX: merged (PR #75);
  see «What exists». The rule that a same-titled block replaces only the blocks it overlaps was confirmed by the
  maintainer on 2026-11-19. No open issue is left to a coding session: #2, #18 (then #19) and the
  device checks #16 / #50 / #51 wait for the maintainer.
- [#47](https://github.com/shoraLBRT/TimeShift/issues/47) — unknown time shown and counted as unknown: merged
  (PR #73); see «What exists».
- [#48](https://github.com/shoraLBRT/TimeShift/issues/48) — ping settings (back-off or stop, on/off, daily quiet
  hours): merged (PR #74); see «What exists».

## Open questions

- LLM provider and model (#2): needs an aggregator key from the maintainer; until then work against
  recorded responses.
- VPS choice for deploy (#18). With the PWA the server can be in Russia; check from the VPS that the push
  hosts and the LLM aggregator are reachable (`docs/deploy.md`).
- #48: the defaults of the new ping settings are the session's choice, not the maintainer's — back-off, pings
  on, no quiet hours, and existing users get the same through the migration (so pings no longer stop for good
  after two unanswered). If the maintainer wants «stop» kept as the default, it is the constant in
  `UserSettings` and the migration default. Option 4 of the issue (one ping per typical duration) is not built,
  as decided.
- #47 was built on the session's reading of «базово предлагается "залипание"» — «Залипание» is the first answer
  offered, not a setting «чем считать неизвестное время» with that default (the issue's option 3). If the
  maintainer meant the setting, it is an addition on top of what is built. Confirming unknown time «as it is»
  (REST `POST /api/confirm` with `includeQuestionable`, the PWA's «Ничего особенного») records it as «Залипание»
  — the same reading, kept from before.
- `/day` and `/week` pictures were checked as rendered PNGs at 720 px, not in Telegram on an iPhone (#16's
  «читается на iPhone» is still to be confirmed by the maintainer). The PWA has no week picture; it has
  `GET /api/summary`.
- Not verified on a real device: the service worker, push delivery and "Add to Home Screen" (need HTTPS and a
  phone); motion smoothness of the three designs was checked by state, not watched on an iPhone.
- Dragging on the timeline (#51) was run in Chromium with pointer and touch events dispatched from script, never
  with a finger. To watch on the phones: that a slow scroll does not turn into a long press, that the page stays
  still once the handles or the new interval appear (iOS Safari is the risky one — it relies on the non-passive
  `touchmove` listener), that the system text-selection / context menu does not show up, and whether the 44 px
  handles are easy to hit. iOS has no vibration API, so the "tick" per step exists on Android only.
- Voice (#50) has never met a real microphone or a real model: no whisper model is configured on the development
  machine, so recognition itself was not run; the browser part was run in Chromium with a synthetic microphone
  (recording → WAV 16 kHz mono → request), the server part with a fake recogniser and synthetic WAV files. Safari
  (AAC, older Web Audio API forms) is written but untested. To watch on the phone: whether the first word is lost
  while the microphone starts (it is opened per phrase, `docs/decisions.md`), and how long whisper `base` takes
  for 30 s on the VPS (the client waits up to 180 s).
- Own categories (#52): standard categories cannot be renamed, hidden or deleted, and an own category has no
  colour of its own (it takes its parent's or its group's) — both left out on purpose (`docs/decisions.md`), say
  if they are wanted. The settings page was checked in Chromium at phone size in the three designs (demo and a
  local server), not on a phone; `/categories` was run through the dialog engine, not in the Telegram app.
- Classification rules (#52): the phrase «ютуб по вечерам — отдых» was run with a scripted model only (no LLM
  key yet, #2): the contract, two eval cases and the prompt example are in place, a real model has not been
  asked. The rule pickers were checked in Chromium at phone size on demo data in the three designs, not on a
  phone; `/facts` — through the dialog engine, not in Telegram.
- #65, left as it was: `IActivityRepository.RecentAsync` / `LastUsedAsync` (the order of the suggestion buttons)
  are still computed from saved entries, so inside the message that starts a brand-new activity the buttons do
  not list it yet. The dialog tests of `TimeShift.Application.Tests` still run on the in-memory
  store, which cannot show this class of error; the scenarios that depend on it are in `DialogTests` on a real
  database — moving more dialog tests there (or running them on both stores) is an option, not done. Two
  requests of the same user handled at the very same moment can still get the same journal time (nothing
  serialises a user's requests); not seen, the owner is one person.
- #67: when an activity already has a category and one message both starts it and re-classifies it («начал
  ютуб, ютуб — это учёба» for a «Ютуб» that was «Отдых»), the entry's category depends on the order the model
  lists the intents in: `start` first — the old one stays on the entry (it is not empty, #62), `classify` first —
  the new one. Same as two separate messages, and not new; say if `classify` should replace the category of an
  entry started by the same message. Likewise «закончил гитару, гитара — это учёба» with `stop` first leaves the
  closed entry without a category (closed entries are not touched, #62). The phrase was run with a scripted
  model only (no LLM key yet, #2).
- #62: the category answer changes the running entry only. The part of the same activity that «Всё ещё занят»
  confirmed before the answer, and an entry that ended before the answer came, stay without a category (an edit
  fixes them) — say if the answer should reach those too. The new reply line and `/undo` after an answer were run
  through the dialog engine and on the database in tests, not in the Telegram app.

## Verify

```sh
dotnet build          # 0 warnings
dotnet test           # all green: 433 tests, 1 skipped (the Postgres-only migration test); databases are SQLite files
TIMESHIFT_TEST_POSTGRES="Host=...;Username=...;Password=..." dotnet test -- --fail-skips on   # same on Postgres: 434, 0 skipped (README; CI runs it)
dotnet run --project src/TimeShift.Host
# http://localhost:5138/            — the app (access key for development: appsettings.Development.json)
# http://localhost:5138/a/?demo=1&at=15:40   — a design on demo data, no login (also /b/, /c/); &speech=0 — without the microphone
```
