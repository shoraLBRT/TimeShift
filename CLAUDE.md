# TimeShift — notes for agent sessions

Telegram bot for tracking lived time. The spec (Russian) is `docs/spec.md` — read it before changing
behaviour; data model is §7, architecture §9.

## Backlog and state

- Backlog: GitHub issues in milestone **MVP** (epic #20); the order of work is `docs/ROADMAP.md`.
- State file: `docs/PROJECT_STATE.md` — what exists, what is next, open questions. Update it in every PR.
- Decisions taken where the spec leaves room: `docs/decisions.md`.
- PRs follow habze: conventional PR title (type from the issue label), squash-merge only.

## Commands

```sh
dotnet build                                   # must stay at 0 warnings (TreatWarningsAsErrors)
dotnet test                                    # xUnit v3 on Microsoft.Testing.Platform (see global.json)
TIMESHIFT_TEST_POSTGRES="Host=...;Username=...;Password=..." dotnet test -- --fail-skips on   # same tests on Postgres (CI runs both)
dotnet run --project src/TimeShift.Host        # Development: SQLite timeshift.db, migrations on start
curl http://localhost:5138/health
docker compose up --build                      # app + postgres
```

Migrations exist per provider — always add both (`SqliteTimeShiftDbContext` → `Persistence/Migrations/Sqlite`,
`PostgresTimeShiftDbContext` → `Persistence/Migrations/Postgres`); full commands in README.md.
`dotnet ef` is a local tool: `dotnet tool restore` first.

Tests that need a database take it from `tests/Shared/TestDatabase.cs` (SQLite file by default, a throwaway
Postgres database when `TIMESHIFT_TEST_POSTGRES` is set) — do not hard-code a provider in a test.

## Layer rule (enforced by project references — do not add shortcuts)

- `TimeShift.Domain` — no project or package references. Entities, timeline rules, invariants.
- `TimeShift.Application` — depends only on Domain. Use cases and ports (IClock, repositories,
  ILlmProvider, INotifier, ...).
- Adapters (`Infrastructure`, `Telegram`) depend on Application. `Host` wires everything together.
- Telegram is just one client. All logic lives in Domain/Application; adapters only translate.

## Web client (PWA)

- REST contract: `docs/web-api.md` (adapter `src/TimeShift.Web`, use cases in `Application/Clients`).
- Frontend: `src/TimeShift.Host/wwwroot` — plain ES modules and CSS, **no build step, no dependencies, no
  requests to other origins**. All logic is in `core/` (its interface: `core/README.md`); a design folder
  (`a/`, `b/`, `c/`) holds only the look. Check a change with `?demo=1&at=15:40` (no server data needed).
- `dotnet run` with a Telegram token in user secrets starts long polling against the real bot — for a
  throwaway run set `Telegram__BotToken=" "`.

## Conventions

- All time is stored in UTC.
- Package versions live only in `Directory.Packages.props` (central package management); stay on the
  10.0.x line of Microsoft packages while targeting net10.0.
