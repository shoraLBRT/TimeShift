# Развёртывание на сервере (VPS) с HTTPS

Приложение, база и HTTPS-прокси поднимаются одной командой через Docker Compose. HTTPS нужен PWA: без него
браузер не даст установить приложение на экран «Домой» и включить уведомления.

## Что нужно

- VPS с Linux (1 ядро и 1 ГБ хватит), Docker и Docker Compose v2 (`docker compose version`).
- Домен или поддомен, например `time.example.ru`.
- Открытые порты 80 и 443 (80 нужен Let's Encrypt для выпуска сертификата).
- По желанию: токен Telegram-бота, ключ LLM-агрегатора.

## Шаги

1. **DNS.** Создайте A-запись `time.example.ru` → IP сервера (для IPv6 — AAAA). Дождитесь, пока она заработает:
   `dig +short time.example.ru`.
2. **Код.** `git clone <репозиторий> timeshift && cd timeshift`.
3. **Настройки.** `cp .env.example .env` и заполните `.env`: `DOMAIN`, `WEB_ACCESS_KEY` (длинная случайная строка,
   например `openssl rand -base64 24`), `POSTGRES_PASSWORD`, при необходимости Telegram и LLM. Каждая переменная
   описана в самом файле.
4. **Запуск.** `docker compose --profile web up -d --build`. Первая сборка занимает несколько минут. Caddy сам
   получит сертификат Let's Encrypt; следите: `docker compose logs -f caddy`.
5. **Закройте прямой порт приложения.** Compose публикует `8080` (так работает запуск без профиля `web`). С HTTPS
   он не нужен снаружи: `sudo ufw allow 80,443/tcp && sudo ufw deny 8080/tcp` (или правило в панели хостера).
6. **Первый вход.** Откройте `https://time.example.ru`, введите значение `WEB_ACCESS_KEY`. Ключ вводится один раз на
   устройстве; смена ключа в `.env` разлогинивает все устройства.

Без профиля `web` (`docker compose up --build`) по-прежнему поднимаются только приложение и база на порту 8080.

## Установка на телефон

- **iPhone (Safari, iOS 16.4+):** откройте сайт → «Поделиться» → «На экран „Домой“» → «Добавить». Уведомления
  включаются только в приложении, открытом с экрана «Домой».
- **Android (Chrome):** меню ⋮ → «Установить приложение» (или «Добавить на главный экран»).

## Уведомления

Откройте приложение с экрана «Домой» → настройки → «Включить уведомления» → разрешите. Кнопка «Проверить»
присылает пробное уведомление. Пинги и вечерний разбор идут и в Telegram (если он настроен), и на подписанные
устройства. Ключи VAPID сервер создаёт сам при первом запуске и хранит в базе — настраивать их не нужно (необязательно
задайте `PUSH_SUBJECT` — контактную почту для push-сервисов).

> **Честно про доставку.** Уведомления идут с вашего сервера на push-сервисы Apple (`web.push.apple.com`) и Google
> (`fcm.googleapis.com`). Если сервер до них не достучится, уведомления не придут, хотя всё остальное работает.
> Проверьте доступность до установки (ниже).

## Проверить с сервера

```sh
# push-сервисы (любой HTTP-ответ, даже 400/404, значит «достучались»; таймаут — нет)
curl -sS -o /dev/null -w 'apple: %{http_code}\n' --max-time 10 https://web.push.apple.com
curl -sS -o /dev/null -w 'fcm:   %{http_code}\n' --max-time 10 https://fcm.googleapis.com

# LLM-агрегатор / провайдер (подставьте свой LLM_BASE_URL)
curl -sS -o /dev/null -w 'llm:   %{http_code}\n' --max-time 10 "$LLM_BASE_URL/models"

# Telegram (нужен, если включён бот)
curl -sS -o /dev/null -w 'tg:    %{http_code}\n' --max-time 10 https://api.telegram.org

# сертификат Let's Encrypt выпущен
docker compose logs caddy | grep -i 'certificate obtained'
curl -sSI https://time.example.ru/health | head -1
```

Сертификат не выпускается — проверьте, что A-запись указывает на этот сервер, а порты 80/443 открыты снаружи.
Если до push-сервисов или Telegram не достучаться, нужен прокси или другой хостинг; для Telegram есть
`TELEGRAM_PROXY`.

## Обновление

```sh
git pull
docker compose --profile web up -d --build
```

Миграции базы применяются при старте автоматически. Откат: `git checkout <тег или коммит>` и та же команда.

## Резервная копия базы

```sh
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' | gzip > timeshift-$(date +%F).sql.gz
```

Восстановление в пустую базу: `gunzip -c timeshift-ГГГГ-ММ-ДД.sql.gz | docker compose exec -T postgres sh -c 'psql -U "$POSTGRES_USER" "$POSTGRES_DB"'`.
Сертификаты и ключи Caddy лежат в томах `caddy_data` и `caddy_config`, ключи VAPID — в базе.
