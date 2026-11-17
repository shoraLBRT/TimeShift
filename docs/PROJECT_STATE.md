# Project state

_Updated 2026-11-17._

## Stage

Two tracks, order in `docs/ROADMAP.md`:

- **MVP (Telegram bot)** — stage 8 (images); steps 1–7 of spec §11 are done.
- **PWA (own mobile client)** — built, waiting for the maintainer to choose one of three designs (#46).

## What exists

- **Domain:** timeline layers (segments → open-activity extension ×norm/×max → week template → background),
  period resolution (§3.5), breaks inside an activity and parallel activities (§3.6), `Timeline.Apply/Stop/
  Continue/Undo`, ping rule `PingSchedule`.
- **Application:** client-neutral dialog `ConversationService` (onboarding, `/blocks`, `/settings`, `/now`,
  `/day [вчера|сб|13.11]` as text with ◀ ▶ and the parallel track, `/add` (past entries without LLM), `/quiet`, `/undo`, short free text = new activity), `TrackingService`, `PingService`.
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
- **Rendering (`TimeShift.Rendering`, SkiaSharp):** `/day` is a PNG (vertical day bar, colour = category, ✅ solid / ⚪ pale / 🟡 hatched, «now» line, parallel column) with the text and «Итого» per category as caption; Docker image installs DejaVu for Cyrillic.
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
  planner), `b/` «Сетка» (iOS calendar grid), `c/` «Нить» (dark typographic thread). `/` is the chooser.
  Ping interval is 5–240 minutes.
- **Deploy:** `docker compose --profile web` adds Caddy with automatic HTTPS; guide in `docs/deploy.md`.
- **Host:** `PingWorker` once a minute (when any notification channel exists), `/health` with version, static
  files, `/api`.
- **Repo:** habze standard (PR-title check, release-please, Renovate) — PR #1; CI build + tests on PRs.

## Next

[#16](https://github.com/shoraLBRT/TimeShift/issues/16) — `/week` image and stats. (#2 waits for the LLM key; voice needs a whisper model on the server).

PWA: [#46](https://github.com/shoraLBRT/TimeShift/issues/46) — the maintainer picks a design; the other two
folders are deleted. Then #50 (voice), #51 (drag the edges of an entry).

## Open questions

- LLM provider and model (#2): needs an aggregator key from the maintainer; until then work against
  recorded responses.
- VPS choice for deploy (#18). With the PWA the server can be in Russia; check from the VPS that the push
  hosts and the LLM aggregator are reachable (`docs/deploy.md`).
- Which PWA design stays (#46).
- Unknown time is counted as «Залипание» (#47); pings stop for good after two unanswered (#48) — both change
  the spec and need a decision.
- Not verified on a real device: the service worker, push delivery and "Add to Home Screen" (need HTTPS and a
  phone); motion smoothness of the three designs was checked by state, not watched on an iPhone.

## Verify

```sh
dotnet build          # 0 warnings
dotnet test           # all green (Domain, Application, Infrastructure on SQLite)
dotnet run --project src/TimeShift.Host
# http://localhost:5138/            — design chooser (access key for development: appsettings.Development.json)
# http://localhost:5138/a/?demo=1&at=15:40   — a design on demo data, no login (also /b/, /c/)
```
