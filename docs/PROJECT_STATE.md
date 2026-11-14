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
  `/day` as text, `/quiet`, `/undo`, short free text = new activity), `TrackingService`, `PingService`.
- **Infrastructure:** EF Core with SQLite (dev) and Postgres (prod), two migration sets; segments, activities,
  undo journal, schedule blocks.
- **Telegram:** long polling, inline keyboards, `TelegramNotifier` for pings; optional proxy and allow-list.
- **LLM (not wired into the dialog yet — #6):** `ILlmProvider` + OpenAI-compatible adapter (`Llm:*`),
  intent contract (`Intents`, `IntentParser`), prompt with context (`IntentPrompt`), `IntentExtractor` with one
  retry, day/time resolution `PastTime`.
- **LLM eval (`tools/TimeShift.LlmEval`):** 40 phrases with expected intents, `IntentMatcher`, live run with
  accuracy/latency/cost and `--record`; offline checks in `dotnet test`. No live run yet (no key, #2).
- **Host:** `PingWorker` once a minute, `/health` with version.
- **Repo:** habze standard (PR-title check, release-please, Renovate) — PR #1; CI build + tests on PRs.

## Next

[#6](https://github.com/shoraLBRT/TimeShift/issues/6) — apply intents: period proposal and confirm buttons.

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
