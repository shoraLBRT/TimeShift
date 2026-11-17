# Web API (v1)

REST API for the PWA client (`src/TimeShift.Host/wwwroot`). The adapter lives in `src/TimeShift.Web`; all
logic stays in Domain/Application. Single-owner mode: one access key opens one user's data.

## Conventions

- Base path `/api`, JSON, camelCase, UTF-8. Unknown fields are ignored.
- **Instant** — ISO 8601 UTC with `Z`: `2026-11-17T11:30:00Z`.
- **Local time** — wall time in the user's time zone, no offset: `2026-11-17T14:30`. `24:00` is not used; the end
  of a day is the next day's `00:00`.
- **Date** — `2026-11-17` (a local day of the user). **Clock** — `HH:mm`.
- Errors: `{ "error": "<code>", "message": "<Russian text for the user>" }`.

| Status | `error` | When |
|---|---|---|
| 400 | `bad_request` | Malformed JSON, wrong types |
| 401 | `unauthorized` | No or expired session |
| 403 | `csrf` | Mutating request without the `X-TimeShift: 1` header |
| 404 | `not_found` | No such entry, block, activity |
| 409 | `conflict` | Domain conflict (overlapping blocks, nothing to stop, not onboarded) |
| 422 | `validation` | A value is out of range; `field` names it |
| 429 | `too_many_requests` | Login attempts |
| 503 | `web_disabled` | `Web:AccessKey` is not configured |

## Auth

Single owner. `Web:AccessKey` (config / env `Web__AccessKey`) is the key the owner types once per device.

- `POST /api/auth/login` `{ "key": "…" }` → `204` + cookie `ts_session` (HttpOnly, SameSite=Lax, Secure on
  https, 400 days). Wrong key → `401`. At most 5 attempts per minute per address → `429`.
- `POST /api/auth/logout` → `204`, cookie cleared.
- The session is stateless: `expiry.signature`, HMAC-SHA256 keyed by a hash of the access key. Changing the key
  signs every device out. It survives restarts and needs no storage.
- Every non-GET request must carry the header `X-TimeShift: 1` (CSRF), login and logout included.
- Which user: `Web:TelegramChatId` if set (the same account as in Telegram, created when missing); otherwise the
  only user in the database if there is exactly one; otherwise the user without Telegram (created when missing).

## Meta

`GET /api/meta` (anonymous)

```json
{
  "version": "0.5.0",
  "assetsVersion": "9f2c1a7e",
  "webEnabled": true,
  "authenticated": false,
  "onboarded": false,
  "llm": true,
  "speech": false,
  "push": { "enabled": true, "publicKey": "BPm…" }
}
```

Until Web Push (#40) is implemented, `push` is `{ "enabled": false, "publicKey": null }`.

`assetsVersion` changes whenever a file under `wwwroot` changes; the client uses it to refresh its cache.

## Profile, settings, week template

`GET /api/me` → **Me**

```json
{
  "id": "…", "displayName": "Пользователь", "timeZone": "Europe/Moscow", "onboarded": true,
  "telegramLinked": false, "quietUntilUtc": null,
  "settings": {
    "pingIntervalMin": 30, "normFactor": 1.5, "maxFactor": 2.0, "maxUnansweredPings": 2,
    "pingDuringBlocks": false, "eveningReviewOffsetMin": 30, "roundingMin": 5
  },
  "limits": {
    "pingIntervalMin": { "min": 5, "max": 240 },
    "maxUnansweredPings": { "min": 1, "max": 10 },
    "eveningReviewOffsetMin": { "min": 0, "max": 180 },
    "roundingMin": [1, 5, 10, 15]
  },
  "sleep": { "start": "00:30", "end": "08:30", "weekend": { "start": "02:00", "end": "10:00" } },
  "blocks": [
    { "id": "…", "kind": "fixed", "title": "Работа", "category": "work",
      "days": [1, 2, 3, 4, 5], "start": "09:00", "end": "18:00" }
  ],
  "categories": [
    { "key": "work", "name": "Работа", "group": "useful", "parent": null },
    { "key": "wasted.youtube", "name": "YouTube", "group": "lost", "parent": "wasted" }
  ]
}
```

- `normFactor` / `maxFactor`: `null` means no limit (∞). `maxFactor >= normFactor > 0`.
- `sleep` is `null` until set; `weekend` is `null` when every night is the same.
- `days`: ISO weekdays, 1 = Monday … 7 = Sunday — the days the block **starts** on. `end < start` crosses midnight.
- `kind`: `fixed` (work, study) or `custom`. Sleep is not in `blocks`.
- `group`: `useful | necessary | people | rest | lost`.

All of the following return the updated **Me**:

| Request | Body |
|---|---|
| `PATCH /api/settings` | any of `displayName`, `timeZone` (IANA) and the `settings` fields, flat |
| `PUT /api/schedule/sleep` | `{ "start": "00:30", "end": "08:30", "weekend": { "start", "end" } \| null }` |
| `POST /api/schedule/blocks` | `{ "kind", "title", "category"?, "days", "start", "end" }` |
| `PUT /api/schedule/blocks/{id}` | the same |
| `DELETE /api/schedule/blocks/{id}` | — |
| `POST /api/onboarding/complete` | — (`409` when sleep is not set) |

A block that overlaps another block or sleep → `409 conflict` with the name of the other block in `message`.
An update keeps the block's `id`; a failed update leaves the block as it was.

## Day

`GET /api/day/{date}` → **Day**

```json
{
  "date": "2026-11-17", "timeZone": "Europe/Moscow", "isToday": true,
  "startUtc": "2026-11-16T21:00:00Z", "endUtc": "2026-11-17T21:00:00Z", "dayMinutes": 1440,
  "nowUtc": "2026-11-17T11:32:10Z", "nowMin": 872,
  "pieces": [
    { "startMin": 0, "endMin": 510, "startUtc": "…", "endUtc": "…",
      "title": "Сон", "category": "sleep", "status": "assumed", "source": "schedule",
      "future": false, "open": false, "activityId": null, "segment": null },
    { "startMin": 540, "endMin": 872, "startUtc": "…", "endUtc": "…",
      "title": "Работа", "category": "work", "status": "assumed", "source": "extension",
      "future": false, "open": true, "activityId": "…",
      "segment": { "id": "…", "startLocal": "2026-11-17T09:00", "endLocal": null } }
  ],
  "parallel": [],
  "current": { "segmentId": "…", "activityId": "…", "title": "Работа", "category": "work",
               "startUtc": "…", "startLocal": "2026-11-17T09:00", "elapsedMin": 152, "typicalMin": 240 },
  "totals": [ { "category": "work", "minutes": 152 } ],
  "status": { "confirmedMin": 0, "assumedMin": 662, "questionableMin": 210 },
  "parallelMin": 0,
  "attention": { "pingPending": false, "quietUntilUtc": null }
}
```

- `pieces` is the main track: ordered, contiguous, covers `[0, dayMinutes)` exactly. `dayMinutes` is 1440 except
  on days when the clocks change.
- `startMin` / `endMin` — minutes from the local start of this day, clipped to the day.
- `nowMin` — `null` when the day is not today.
- `status`: `confirmed | assumed | questionable`. `source`: `user | extension | schedule | background | import`.
- `future: true` — after "now": a forecast from the week template, not a fact.
- `open: true` — the piece belongs to the running activity.
- `segment` — the stored segment behind the piece (`null` for computed pieces: template blocks and background).
  `startLocal` / `endLocal` are its full bounds (it may start on another day); `endLocal` is `null` while it runs.
  One piece maps to exactly one stored segment: neighbours from different segments are not merged.
- `current` — the running activity, whatever day is shown; `null` when nothing runs.
- `totals`, `status`, `parallelMin` count the past part of the main track only (up to "now"); `totals` is ordered
  by minutes, descending. `category` may be `null` (not classified yet).
- A day in the future is allowed (all pieces `future`); it shows the week template.
- `attention.pingPending` is `true` while the last ping is unanswered. A malformed date → `422` with `field: "date"`
  (`view` for the `?view=` parameter).

`GET /api/summary?from=2026-11-11&to=2026-11-17` (at most 31 days; `to` defaults to today, `from` to six days before `to`) → totals per day and for the whole range:

```json
{
  "from": "2026-11-11", "to": "2026-11-17",
  "days": [ { "date": "2026-11-11", "totals": [ { "category": "work", "minutes": 430 } ],
              "status": { "confirmedMin": 900, "assumedMin": 480, "questionableMin": 60 }, "parallelMin": 30 } ],
  "totals": [ { "category": "sleep", "minutes": 3300 } ],
  "status": { "confirmedMin": 6200, "assumedMin": 3300, "questionableMin": 580 }
}
```

## Changing the timeline

Every request below returns `{ "day": Day }` — the refreshed day. Which day: query `?view=YYYY-MM-DD`
(default — today). Each of them counts as "the user answered" (unanswered pings reset) and is one step for undo.

| Request | Body | What it does |
|---|---|---|
| `POST /api/now/start` | `{ "activityId" }` or `{ "title" }`, optional `"at"` (local time, not in the future) | Starts an activity now (or from `at`); the running one is closed. A new title creates the activity. |
| `POST /api/now/stop` | optional `"at"` | Stops the running activity. `409` when nothing runs. |
| `POST /api/now/continue` | — | "Still doing it": confirms the running activity up to now. `409` when nothing runs. |
| `POST /api/entries` | `{ "start", "end", "title" \| "activityId", "category"?, "parallel"? }` | Records a past period as confirmed; it cuts and displaces what was there (parallel entries displace nothing). |
| `PATCH /api/entries/{segmentId}` | any of `start`, `end`, `title`, `category` | Edits a stored segment. Freed time falls back to the template / background. |
| `DELETE /api/entries/{segmentId}` | — | Removes a stored segment. |
| `POST /api/confirm` | `{ "start", "end", "includeQuestionable"? }` | Confirms computed pieces in the range as they are shown (assumed → confirmed; questionable only when `includeQuestionable`). Stored segments are left alone. The running activity is confirmed as a whole up to now (like `continue`), and only together with `includeQuestionable` when part of it is questionable; other time inside the running activity is not touched. |
| `POST /api/undo` | — | Reverts the last change. Adds `"result": "undone" \| "nothing" \| "stale"`. |
| `POST /api/quiet` | `{ "minutes": 120 }` (`0` — off; 5…1440) | Manual quiet hours. |

- `start` / `end` are local times; `end > start`; the future is rejected with `422` ("Будущее не записывается").
- `category` is a category key; omitted for a known activity — its default category.
- `title` is 1–60 characters.

## Activities

- `GET /api/activities` → `[ { "id", "name", "category", "typicalMin", "lastUsedUtc" } ]`, most recently used
  first (at most 200).
- `PATCH /api/activities/{id}` `{ "name"?, "category"?, "typicalMin"? }` → the activity. `typicalMin: null`
  clears the typical duration; otherwise 1…1440.

## Free text

`POST /api/messages` — `{ "text": "час тренировался, потом поехал домой" }` or `{ "button": "per:ok:…" }`.
The same dialog engine as in Telegram (`ConversationService`): with an LLM key the phrase is parsed into
intents, without it a short text starts an activity and `/add`-style lines record the past.

```json
{
  "replies": [ { "text": "Записать так?\n14:00–15:00 Тренировка…", "replacesSource": false, "hasImage": false,
                 "buttons": [ [ { "label": "✅ Верно", "data": "per:ok:…" }, { "label": "✏️ Поправить", "data": "per:fix:…" } ] ] } ],
  "day": { }
}
```

Exactly one of `text` / `button`. Voice is not accepted here. `409` when the owner has not finished onboarding (the PWA has its own onboarding screens).

## Push

Web Push with VAPID; enabled whenever the web client is (`Push:Subject` defaults to `mailto:owner@localhost`). Keys: `Push:PublicKey` / `Push:PrivateKey` / `Push:Subject` from config; when absent they
are generated once and kept in the database.

- `GET /api/push` → `{ "enabled": true, "publicKey": "…", "subscriptions": 1 }`
- `POST /api/push/subscriptions` — `{ "endpoint", "keys": { "p256dh", "auth" } }` → `204` (idempotent by endpoint)
- `DELETE /api/push/subscriptions` — `{ "endpoint" }` → `204`
- `POST /api/push/test` → `204`; `409` when there are no subscriptions

Payload delivered to the service worker:

```json
{ "kind": "ping", "title": "Всё ещё «Работа»?", "body": "Идёт 1 ч 20 мин", "tag": "ts-ping", "url": "/?from=ping",
  "actions": [ { "action": "continue", "title": "Продолжаю" }, { "action": "stop", "title": "Закончил" } ] }
```

`kind`: `ping | evening | test`. A new notification with the same `tag` replaces the previous one (the same rule
as "the previous unanswered ping is deleted" in Telegram). `actions` are present only for a ping during a
running activity; platforms without notification buttons (iOS) ignore them. A subscription that the push
service reports as gone (404 / 410) is deleted.
