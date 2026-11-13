# TimeShift — notes for agent sessions

Telegram bot for tracking lived time. The spec (Russian) is `docs/spec.md` — read it before changing
behaviour; data model is §7, architecture §9.

## Commands

```sh
dotnet build                                   # must stay at 0 warnings (TreatWarningsAsErrors)
dotnet test                                    # xUnit v3 on Microsoft.Testing.Platform (see global.json)
dotnet run --project src/TimeShift.Host        # Development: SQLite timeshift.db, migrations on start
curl http://localhost:5138/health
docker compose up --build                      # app + postgres
```

Migrations exist per provider — always add both (`SqliteTimeShiftDbContext` → `Persistence/Migrations/Sqlite`,
`PostgresTimeShiftDbContext` → `Persistence/Migrations/Postgres`); full commands in README.md.
`dotnet ef` is a local tool: `dotnet tool restore` first.

## Layer rule (enforced by project references — do not add shortcuts)

- `TimeShift.Domain` — no project or package references. Entities, timeline rules, invariants.
- `TimeShift.Application` — depends only on Domain. Use cases and ports (IClock, repositories,
  ILlmProvider, INotifier, ...).
- Adapters (`Infrastructure`, `Telegram`) depend on Application. `Host` wires everything together.
- Telegram is just one client. All logic lives in Domain/Application; adapters only translate.

## Conventions

- All time is stored in UTC.
- Package versions live only in `Directory.Packages.props` (central package management); stay on the
  10.0.x line of Microsoft packages while targeting net10.0.
