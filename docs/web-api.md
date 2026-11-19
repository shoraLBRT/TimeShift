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
  "speechMaxSec": 120,
  "push": { "enabled": true, "publicKey": "BPm…" }
}
```

Until Web Push (#40) is implemented, `push` is `{ "enabled": false, "publicKey": null }`.

`assetsVersion` changes whenever a file under `wwwroot` changes; the client uses it to refresh its cache.

`speech` — the server has a speech model and accepts voice in `POST /api/messages`; the client shows the microphone
only then. `speechMaxSec` — the longest recording it takes (`Speech:MaxSeconds`).

## Profile, settings, week template

`GET /api/me` → **Me**

```json
{
  "id": "…", "displayName": "Пользователь", "timeZone": "Europe/Moscow", "onboarded": true,
  "telegramLinked": false, "quietUntilUtc": null,
  "settings": {
    "pingIntervalMin": 30, "normFactor": 1.5, "maxFactor": 2.0, "maxUnansweredPings": 2,
    "pingDuringBlocks": false, "eveningReviewOffsetMin": 30, "roundingMin": 5,
    "pingsEnabled": true, "afterUnansweredPings": "backOff", "quietHours": { "start": "22:00", "end": "08:00" }
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
    { "key": "work", "name": "Работа", "group": "useful", "parent": null, "system": true, "archived": false },
    { "key": "wasted.youtube", "name": "YouTube", "group": "lost", "parent": "wasted", "system": true, "archived": false },
    { "key": "u.3fa9c1d2e0", "name": "Музыка", "group": "rest", "parent": null, "system": false, "archived": false }
  ]
}
```

- `normFactor` / `maxFactor`: `null` means no limit (∞). `maxFactor >= normFactor > 0`.
- Pings (spec §5.2): `pingsEnabled: false` — no pings at all (the evening review still comes).
  `afterUnansweredPings` — what happens after `maxUnansweredPings` unanswered pings in a row: `backOff` (default;
  each next interval is twice as long, at most 4 h, the first answer restores the interval) or `stop` (no pings
  until the user writes). `quietHours` — daily hours without pings in the user's time zone, `end` not included,
  `end < start` crosses midnight; `null` (default) — none. In `PATCH /api/settings` `quietHours: null` removes
  them; `start == end` is a 422 on `quietHours`.
- `sleep` is `null` until set; `weekend` is `null` when every night is the same.
- `days`: ISO weekdays, 1 = Monday … 7 = Sunday — the days the block **starts** on. `end < start` crosses midnight.
- `kind`: `fixed` (work, study) or `custom`. Sleep is not in `blocks`.
- `group`: `useful | necessary | people | rest | lost`.
- `categories` is the whole set of this user: the standard ones (`system: true`, fixed) and the user's own
  (`system: false`, keys start with `u.` and never change). `archived: true` marks an own category that was
  deleted: it is not offered for new entries and is rejected as a `category` value, but stays in the list so that
  old entries, which keep its key, still have a name, a group and a parent. `parent` is one level deep.

All of the following return the updated **Me**:

| Request | Body |
|---|---|
| `PATCH /api/settings` | any of `displayName`, `timeZone` (IANA) and the `settings` fields, flat |
| `PUT /api/schedule/sleep` | `{ "start": "00:30", "end": "08:30", "weekend": { "start", "end" } \| null }` |
| `POST /api/schedule/blocks` | `{ "kind", "title", "category"?, "days", "start", "end" }` |
| `PUT /api/schedule/blocks/{id}` | the same |
| `DELETE /api/schedule/blocks/{id}` | — |
| `POST /api/categories` | `{ "name", "group" }` — an own category, or `{ "name", "parent" }` — a subcategory |
| `PATCH /api/categories/{key}` | any of `name`, `group`, `parent` (`null` makes a subcategory a category of its own) |
| `DELETE /api/categories/{key}` | — |
| `POST /api/onboarding/complete` | — (`409` when sleep is not set) |

A block that overlaps another block or sleep → `409 conflict` with the name of the other block in `message`.
An update keeps the block's `id`; a failed update leaves the block as it was.

Own categories:

- `name` is 1–40 characters and unique among the categories that are not deleted (case and «ё» ignored) — `422`
  with `field: "name"`. At most 50 own categories.
- `parent` is the key of a top-level category, standard or own; the subcategory takes the parent's `group`
  (a `group` sent along is ignored) and is counted with the parent wherever totals are folded. A category that has
  subcategories cannot become one — `422` with `field: "parent"`. Without `parent`, `group` is required.
- Changing the `group` of a category moves its subcategories with it. The `key` stays the same through every edit.
- Standard categories cannot be changed or deleted — `409`; an unknown or already deleted key — `404`.
- `DELETE` archives the category and its own subcategories (see `archived` above). Stored entries are not
  touched; activities that had it as their default category lose it (the next `GET /api/activities` shows
  `category: null`) and lose the rules that led to it, week-template blocks with it stay without a category.
- `POST` with the name of a deleted category brings that category back with its old key, so old and new entries
  are counted together again (its deleted subcategories stay deleted).

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
  "unknownMin": 60,
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
- `current` — the running activity, whatever day is shown; `null` when nothing runs. Its `category` is the
  category of the running entry itself, the same value as in its piece and in `totals` (`null` when the entry has
  none) — not the activity's default category, which is in `GET /api/activities`.
- Background pieces (`source: "background"`) are unknown time: `title` «Не записано», `category: null`, status
  `questionable` (#47). They are not «Залипание» and not part of the `lost` group.
- `totals`, `status`, `unknownMin`, `parallelMin` count the past part of the main track only (up to "now"); `totals`
  is ordered by minutes, descending. `category` may be `null` (not classified yet). Unknown time is **not** in
  `totals`: it is `unknownMin`, so `totals` + `unknownMin` = the past part of the day. It is in `status`
  (`questionableMin`).
- A day in the future is allowed (all pieces `future`); it shows the week template.
- `attention.pingPending` is `true` while the last ping is unanswered. A malformed date → `422` with `field: "date"`
  (`view` for the `?view=` parameter).

`GET /api/summary?from=2026-11-11&to=2026-11-17` (at most 31 days; `to` defaults to today, `from` to six days before `to`) → totals per day and for the whole range:

```json
{
  "from": "2026-11-11", "to": "2026-11-17",
  "days": [ { "date": "2026-11-11", "totals": [ { "category": "work", "minutes": 430 } ],
              "status": { "confirmedMin": 900, "assumedMin": 480, "questionableMin": 60 }, "unknownMin": 30, "parallelMin": 30 } ],
  "totals": [ { "category": "sleep", "minutes": 3300 } ],
  "status": { "confirmedMin": 6200, "assumedMin": 3300, "questionableMin": 580 },
  "unknownMin": 410
}
```

`unknownMin` (per day and for the range) is unknown time, counted apart from `totals` as in the day.

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
| `POST /api/confirm` | `{ "start", "end", "includeQuestionable"? }` | Confirms computed pieces in the range as they are shown (assumed → confirmed; questionable only when `includeQuestionable`). Stored segments are left alone. Unknown time (background) confirmed this way is recorded as «Залипание» (`wasted`) — what the bot offers first for such a span (#47). The running activity is confirmed as a whole up to now (like `continue`), and only together with `includeQuestionable` when part of it is questionable; other time inside the running activity is not touched. |
| `POST /api/undo` | — | Reverts the last change. Adds `"result": "undone" \| "nothing" \| "stale"`. |
| `POST /api/quiet` | `{ "minutes": 120 }` (`0` — off; 5…1440) | Manual quiet hours. |

- `start` / `end` are local times; `end > start`; the future is rejected with `422` ("Будущее не записывается").
- `category` is the key of a category that is not deleted (standard or own), otherwise `422` with
  `field: "category"`; omitted for a known activity — the category of its rule for the part of the day the entry
  starts in, otherwise its default category (see "Activities"). `now/start` takes no category and follows the
  same order. A `PATCH` that changes `title` to another activity takes that activity's category the same way
  unless `category` is sent too.
- `title` is 1–60 characters.

## Activities

- `GET /api/activities` → `[ { "id", "name", "category", "typicalMin", "lastUsedUtc", "rules" } ]`, most recently
  used first (at most 200). `rules` — `[ { "part": "evening", "category": "rest" } ]`, at most one per part of
  the day, from morning to night; empty when the activity has none.
- `PATCH /api/activities/{id}` `{ "name"?, "category"?, "typicalMin"? }` → the activity. `typicalMin: null`
  clears the typical duration; otherwise 1…1440.
- `PUT /api/activities/{id}/rules/{part}` `{ "category" }` → the activity. Sets (or replaces) the classification
  rule "in this part of the day this activity is this category" (spec §4.2: «YouTube по вечерам — отдых»).
- `DELETE /api/activities/{id}/rules/{part}` → the activity; no error when there was no such rule.

A rule depends on the time of day only. `part` is one of four fixed parts of the day in the user's time zone:

| `part` | local time |
|---|---|
| `morning` | 06:00–12:00 |
| `afternoon` | 12:00–18:00 |
| `evening` | 18:00–24:00 |
| `night` | 00:00–06:00 |

The part is taken by the **start** of an entry (17:30–18:30 is `afternoon`). When an entry of the activity is
recorded without a category — by any client, the bot included — it gets the rule's category, and only without a
matching rule the activity's `category`. Rules act on new entries: adding, changing or removing one never
rewrites stored entries. An unknown `part` → `422` with `field: "part"`; a category that does not exist or is
deleted → `422` with `field: "category"`; an unknown activity → `404`. The same rules are listed and removed in
the bot's `/facts`.

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

Exactly one of `text` / `button`. `409` when the owner has not finished onboarding (the PWA has its own onboarding screens).

### Voice

`POST /api/messages` with `Content-Type: audio/wav` — the body is the recording itself, not JSON: **WAV, PCM 16-bit,
mono, 16 kHz** (the PWA re-encodes on the device, `core/voice.js`). `X-TimeShift: 1` and `?view=` work as for text.
The recording is recognised and then handled exactly like a text message; the response has the same shape and
**starts with what was heard**:

```json
{
  "replies": [ { "text": "🎙 «час тренировался, потом поехал домой»", "replacesSource": false, "hasImage": false, "buttons": [] },
               { "text": "Записать так?\n14:00–15:00 Тренировка…", "replacesSource": false, "hasImage": false, "buttons": [ [ ] ] } ],
  "day": { }
}
```

- Nothing heard, or recognition failed — still `200` with a single reply that says so («Не расслышал…»,
  «Не получилось распознать…»); the timeline is not touched.
- `409 conflict` — no speech model on the server (`meta.speech` is `false`), or onboarding is not finished.
- `422 validation`, `field: "audio"` — an empty body, not a WAV file, or longer than `meta.speechMaxSec` (the size
  limit is 44 + `speechMaxSec` × 32 000 bytes; the rest of a larger body is not read).
- Other content types (`audio/x-wav`, `audio/wave`, `audio/vnd.wave` are synonyms) go the JSON way.
- Recognition takes seconds to tens of seconds; recordings are recognised one at a time.

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
