# TimeShift

Telegram-бот для учёта прожитого времени: бот пингует, пользователь коротко отвечает
(текстом или голосом), из ответов собирается лента дня без дыр. Полное ТЗ — [docs/spec.md](docs/spec.md).

Сейчас готов только каркас решения: слои, БД (пользователь и его настройки), `/health`,
Docker. Готовы ядро ленты (Domain) и Telegram-бот: онбординг, `/blocks`, `/settings`, пинги, запись занятий
кнопками и коротким текстом, `/now`, `/day` (текстом), `/quiet`, `/undo`. LLM и распознавания речи пока нет.

## Структура

```
src/
  TimeShift.Domain          сущности и правила, без внешних зависимостей
  TimeShift.Application     сценарии и интерфейсы (IClock, далее ILlmProvider, INotifier, ...)
  TimeShift.Infrastructure  EF Core: SQLite (разработка) / PostgreSQL (прод)
  TimeShift.Telegram        адаптер Telegram: long polling, кнопки → ConversationService (Application)
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
`Telegram:BotToken`: локально — user-secrets (`dotnet user-secrets set "Telegram:BotToken" "<token>" --project src/TimeShift.Host`),
на сервере — переменной окружения `Telegram__BotToken`.

## Конфигурация

| Ключ | Значения |
|---|---|
| `Database:Provider` | `Sqlite` \| `Postgres` |
| `ConnectionStrings:Default` | строка подключения |
| `Database:MigrateOnStartup` | применять миграции при старте (`true` в Development и в compose) |
| `Telegram:BotToken` | токен бота; пусто — Telegram выключен |
| `Telegram:Proxy` | прокси до api.telegram.org (`socks5://127.0.0.1:1080`, `http://…`); пусто — напрямую |
| `Telegram:AllowedChatIds` | список chat id, которым можно пользоваться ботом; пусто — всем |
| `Llm:BaseUrl` | OpenAI-совместимый API (агрегатор: ProxyAPI, AITunnel, Polza, или провайдер напрямую) |
| `Llm:ApiKey` | ключ; пусто — LLM выключен, бот работает кнопками и коротким текстом |
| `Llm:Model` | модель, например `deepseek-chat`, `qwen-flash`, `claude-haiku-5-5` |
| `Llm:TimeoutSeconds`, `Llm:MaxTokens`, `Llm:Temperature`, `Llm:JsonMode` | тонкая настройка (30 с, 800, 0, true) |

## Оценка LLM на тестовом наборе

Набор фраз с ожидаемыми намерениями — `tools/TimeShift.LlmEval/cases.json` (spec §8). Офлайн он проверяется
в `dotnet test`. Живой прогон тратит деньги (~40 запросов) и берёт ключ из секретов Host:

```sh
dotnet user-secrets set "Llm:ApiKey" "<ключ>" --project src/TimeShift.Host
dotnet user-secrets set "Llm:BaseUrl" "https://api.proxyapi.ru/openai/v1" --project src/TimeShift.Host
dotnet user-secrets set "Llm:Model" "deepseek-chat" --project src/TimeShift.Host
dotnet run --project tools/TimeShift.LlmEval -- --price-in 0.14 --price-out 0.28 --record
```

Печатает точность (цель ≥ 90%), задержку, токены и цену; `--record` сохраняет ответы модели в
`tools/TimeShift.LlmEval/recorded/<модель>.json` — дальше `dotnet test` проверяет их без сети. `--only id1,id2` —
прогнать отдельные кейсы.

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
