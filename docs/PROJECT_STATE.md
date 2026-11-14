# Project state

_Updated 2026-11-14._

## Stage

MVP, stage 5 (LLM). Steps 1–4 of spec §11 are done; the backlog is in GitHub issues, order in
`docs/ROADMAP.md`.

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
  LLM call is logged in `messages`. Edits by text (#14) answer "later".
- **LLM building blocks:** `ILlmProvider` + OpenAI-compatible adapter (`Llm:*`),
  intent contract (`Intents`, `IntentParser`), prompt with context (`IntentPrompt`), `IntentExtractor` with one
  retry, day/time resolution `PastTime`.
- **LLM eval (`tools/TimeShift.LlmEval`):** 40 phrases with expected intents, `IntentMatcher`, live run with
  accuracy/latency/cost and `--record`; offline checks in `dotnet test`. No live run yet (no key, #2).
- **Classification (`Domain.Categories.Classifier`):** spec §4.2 defaults by name; YouTube, games and unknown activities get a one-time category question (buttons or a category name).
- **Questions (`QuestionService`, `pending_questions`):** category, then unknown typical duration of the current activity is asked once, right after a reply, one at a time; answers by button or a bare duration; `/facts` lists durations, categories, routes.
- **Speech (`TimeShift.Speech`):** voice → OGG/Opus decoded in C# (Concentus) → local whisper.cpp (Whisper.net); `Speech:ModelPath`, `Speech:AutoDownload`; off without a model.
- **Host:** `PingWorker` once a minute, `/health` with version.
- **Repo:** habze standard (PR-title check, release-please, Renovate) — PR #1; CI build + tests on PRs.

## Next

[#12](https://github.com/shoraLBRT/TimeShift/issues/12) — resolve yellow right after a gap. (#2 waits for the LLM key; voice needs a whisper model on the server).

## Open questions

- LLM provider and model (#2): needs an aggregator key from the maintainer; until then work against
  recorded responses.
- VPS choice for deploy (#18).

## Verify

```sh
dotnet build          # 0 warnings
dotnet test           # all green (Domain, Application, Infrastructure on SQLite)
dotnet run --project src/TimeShift.Host
```
