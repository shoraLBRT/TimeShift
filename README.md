# TimeShift

Telegram-бот для учёта прожитого времени: бот пингует, пользователь коротко отвечает
(текстом или голосом), из ответов собирается лента дня без дыр. Полное ТЗ — [docs/spec.md](docs/spec.md).

Сейчас готов только каркас решения: слои, БД (пользователь и его настройки), `/health`,
Docker. Логики ленты, Telegram-бота, LLM и распознавания речи пока нет.

## Структура

```
src/
  TimeShift.Domain          сущности и правила, без внешних зависимостей
  TimeShift.Application     сценарии и интерфейсы (IClock, далее ILlmProvider, INotifier, ...)
  TimeShift.Infrastructure  EF Core: SQLite (разработка) / PostgreSQL (прод)
  TimeShift.Telegram        адаптер Telegram (пока только регистрация в DI)
  TimeShift.Host            ASP.NET Core: REST API, фоновые сервисы, Telegram в одном процессе
tests/
  TimeShift.Domain.Tests
  TimeShift.Application.Tests
```

## Требования

- .NET SDK 10
- Docker (для запуска через compose)

## Сборка и тесты

```sh
dotnet build
dotnet test
```

Тесты работают на Microsoft.Testing.Platform (xUnit v3), это включено в `global.json`.

## Локальный запуск

```sh
dotnet run --project src/TimeShift.Host
curl http://localhost:5138/health   # {"status":"ok"}
```

В окружении Development используется SQLite-файл `src/TimeShift.Host/timeshift.db`, миграции
применяются при старте (`Database:MigrateOnStartup`).

Без токена Telegram-адаптер выключен (в логе будет предупреждение). Токен задаётся через
`Telegram:BotToken` — например, переменной окружения `Telegram__BotToken`.

## Конфигурация

| Ключ | Значения |
|---|---|
| `Database:Provider` | `Sqlite` \| `Postgres` |
| `ConnectionStrings:Default` | строка подключения |
| `Database:MigrateOnStartup` | применять миграции при старте (`true` в Development и в compose) |
| `Telegram:BotToken` | токен бота; пусто — Telegram выключен |

## Docker Compose

```sh
docker compose up --build
curl http://localhost:8080/health
```

Поднимаются `app` и `postgres` (данные в томе `pgdata`). Пароль БД и токен можно задать в `.env`:

```
POSTGRES_PASSWORD=...
TELEGRAM_BOT_TOKEN=...
```

## Миграции

Миграции ведутся отдельно для каждого провайдера (`Persistence/Migrations/Sqlite` и `.../Postgres`).
После изменения модели добавляйте миграцию для обоих:

```sh
dotnet tool restore
dotnet ef migrations add <Name> -p src/TimeShift.Infrastructure -s src/TimeShift.Host --context SqliteTimeShiftDbContext -o Persistence/Migrations/Sqlite
dotnet ef migrations add <Name> -p src/TimeShift.Infrastructure -s src/TimeShift.Host --context PostgresTimeShiftDbContext -o Persistence/Migrations/Postgres
```
