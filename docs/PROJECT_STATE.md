# Project state

_Updated 2026-11-17._

## Stage

Two tracks, order in `docs/ROADMAP.md`:

- **MVP (Telegram bot)** — stage 9 (deploy); steps 1–8 of spec §11 are done.
- **PWA (own mobile client)** — built; all three designs stay and are switched in the app settings.

## What exists

- **Domain:** timeline layers (segments → open-activity extension ×norm/×max → week template → background),
  period resolution (§3.5), breaks inside an activity and parallel activities (§3.6), `Timeline.Apply/Stop/
  Continue/Undo`, ping rule `PingSchedule`.
- **Application:** client-neutral dialog `ConversationService` (onboarding, `/blocks`, `/settings`, `/now`,
  `/day [вчера|сб|13.11]` as text with ◀ ▶ and the parallel track, `/week` (◀ ▶ by week), `/add` (past entries without LLM), `/quiet`, `/undo`, short free text = new activity), `TrackingService`, `PingService`.
- **Infrastructure:** EF Core with SQLite (dev) and Postgres (prod), two migration sets; segments, activities,
  undo journal, schedule blocks.
- **Telegram:** long polling, inline keyboards, `TelegramNotifier` for pings; optional proxy and allow-list.
- **LLM in the dialog (`IntentApplier`, on when `Llm:ApiKey` is set):** free text → intents; periods are
  proposed with ✅/✏️ (⚪ after 10 min or on the next message), a missing route fact is asked once and remembered
  (`user_facts`), start/stop/quiet/facts/classify apply at once, parallel items go to the parallel track; every
  LLM call is logged in `messages`. `edit_segment` finds the segment by day / time inside / name and changes boundaries, name or category (`Timeline.Edit`).
- **LLM building blocks:** `ILlmProvider` + OpenAI-compatible adapter (`Llm:*`),
  intent contract (`Intents`, `IntentParser`), prompt with context (`IntentPrompt`), `IntentExtractor` with one
  retry, day/time resolution `PastTime`.
- **LLM eval (`tools/TimeShift.LlmEval`):** 40 phrases with expected intents, `IntentMatcher`, live run with
  accuracy/latency/cost and `--record`; offline checks in `dotnet test`. No live run yet (no key, #2).
- **Daily review (`DailyReview`):** evening — by the ping loop at «sleep − offset» (up to 30 min after sleep starts) or when sleep is started: the day, remaining 🟡 one by one, «⚪ Остальное верно» materializes ⚪ as ✅; morning — first message after the template sleep: «Спал как обычно?» (yes → ✅ sleep, no → «во сколько лёг и встал»). Once a day each; nothing before registration.
- **Yellow review (`YellowReview`):** after a text/voice message or an answer button, the first unreviewed 🟡 span (≥ 15 min, last 12 h, after onboarding) is asked «Пока тебя не было…» — recent activity / wasted / describe (`/add`) / skip; each span once (`users.yellow_reviewed_until`).
- **Classification (`Domain.Categories.Classifier`):** spec §4.2 defaults by name; YouTube, games and unknown activities get a one-time category question (buttons or a category name).
- **Questions (`QuestionService`, `pending_questions`):** category, then unknown typical duration of the current activity is asked once, right after a reply, one at a time; answers by button or a bare duration; `/facts` lists durations, categories, routes.
- **Rendering (`TimeShift.Rendering`, SkiaSharp):** `/day` is a PNG (vertical day bar, colour = category, ✅ solid / ⚪ pale / 🟡 hatched, «now» line, parallel column) with the text and «Итого» per category as caption; `/week` is a PNG (a strip per day, a bar per category with hours and the difference with the previous week, one bar for the ✅/⚪/🟡 shares, parallel total) with the same numbers as caption; Docker image installs DejaVu for Cyrillic.
- **Week numbers (`WeekStats`):** calendar week Mon–Sun, the current one up to «now»; built from the same day windows as `/day`, so the week equals the sum of its days; days before registration are not counted; comparison with the same part of the previous week when that week is wholly after registration (`docs/decisions.md`).
- **Speech (`TimeShift.Speech`):** voice → OGG/Opus decoded in C# (Concentus) → local whisper.cpp (Whisper.net); `Speech:ModelPath`, `Speech:AutoDownload`; off without a model.
- **Web API (`TimeShift.Web`, contract `docs/web-api.md`):** single-owner access key with a stateless signed
  session cookie; day (pieces mapped to stored segments), summary, start / stop / continue, entries, confirm,
  undo, quiet, activities, settings, week template, free text through the same dialog engine. Use cases live in
  `Application/Clients`. A user can exist without Telegram (`telegram_chat_id` is nullable).
- **Notifications:** channels fan-out (`NotificationFanout`): Telegram and Web Push (VAPID keys from config or
  generated once and kept in the database, subscriptions in `push_subscriptions`).
- **PWA (`src/TimeShift.Host/wwwroot`, no build step, no third-party requests):** shared core (`core/`: API
  client, in-memory demo backend `?demo=1&at=15:40`, state, timeline layout, bottom sheet, toasts, gestures,
  functional panels, push, service worker) and three complete design MVPs on top of it — `a/` «Лист» (paper
  planner), `b/` «Сетка» (iOS calendar grid), `c/` «Нить» (dark typographic thread). All three stay: the look is switched in Настройки → Прочее → Вид;
  `/` opens the stored one (the chooser on the first run).
  Ping interval is 5–240 minutes.
- **Deploy:** `docker compose --profile web` adds Caddy with automatic HTTPS; guide in `docs/deploy.md`.
- **Host:** `PingWorker` once a minute (when any notification channel exists), `/health` with version, static
  files, `/api`.
- **Repo:** habze standard (PR-title check, release-please, Renovate) — PR #1; CI build + tests on PRs.
- **Tests on both databases (#17):** every test that needs a database gets it from `tests/Shared/TestDatabase.cs`
  — a temporary SQLite file, or its own Postgres database when `TIMESHIFT_TEST_POSTGRES` is set. CI runs the
  suite twice, on SQLite and on a `postgres:17` service container (Postgres migrations applied from scratch for
  every test; a skipped Postgres-only test fails that run). `MigrationTests` also fail, without any database,
  when the model is ahead of either migration set.

## Next

[#19](https://github.com/shoraLBRT/TimeShift/issues/19) — logs, DB backups, monitoring.
[#18](https://github.com/shoraLBRT/TimeShift/issues/18) (deploy to a VPS) comes first in the roadmap but waits for
the maintainer to choose and pay for the VPS. (#2 waits for the LLM key; voice needs a whisper model on the server).

PWA: check on a real phone over HTTPS (#45), then #50 (voice), #51 (drag the edges of an entry).

## Open questions

- LLM provider and model (#2): needs an aggregator key from the maintainer; until then work against
  recorded responses.
- VPS choice for deploy (#18). With the PWA the server can be in Russia; check from the VPS that the push
  hosts and the LLM aggregator are reachable (`docs/deploy.md`).
- Unknown time is counted as «Залипание» (#47); pings stop for good after two unanswered (#48) — both change
  the spec and need a decision.
- `/day` and `/week` pictures were checked as rendered PNGs at 720 px, not in Telegram on an iPhone (#16's
  «читается на iPhone» is still to be confirmed by the maintainer). The PWA has no week picture; it has
  `GET /api/summary`.
- Not verified on a real device: the service worker, push delivery and "Add to Home Screen" (need HTTPS and a
  phone); motion smoothness of the three designs was checked by state, not watched on an iPhone.

## Verify

```sh
dotnet build          # 0 warnings
dotnet test           # all green: 321 tests, 1 skipped (the Postgres-only migration test); databases are SQLite files
TIMESHIFT_TEST_POSTGRES="Host=...;Username=...;Password=..." dotnet test -- --fail-skips on   # same on Postgres: 321, 0 skipped (README)
dotnet run --project src/TimeShift.Host
# http://localhost:5138/            — the app (access key for development: appsettings.Development.json)
# http://localhost:5138/a/?demo=1&at=15:40   — a design on demo data, no login (also /b/, /c/)
```
