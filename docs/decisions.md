# Decisions

Short log of choices made where the spec (`docs/spec.md`) leaves room. One bullet per decision, with the reason.

## Step 2 — timeline core (Domain)

### Model

- **Time type: `DateTime` with `Kind = Utc`**, validated in every constructor. Matches the existing
  `User.CreatedAtUtc` and maps to EF Core without converters.
- **`Segment` has no id yet.** It is an immutable value (record); identity is added with the EF mapping. Value
  equality is enough for `Timeline.Undo`, because materialized segments never overlap.
- **Categories are referenced by stable string keys** (`sleep`, `wasted.short_video`, …) from
  `DefaultCategories`; user-defined categories come later with persistence.
- **A schedule block's days of week are the days it starts on**; `End < Start` means it crosses midnight. Overlap
  validation runs on a 7-day ring, so a Sunday-night block that runs into Monday is checked against Monday blocks.
- **Custom extension policy: `norm > 0`, `max ≥ norm`**, either can be `+∞`. `NaN` is rejected.

### Time zones / DST

- **A local time that does not exist (spring forward) maps to the transition instant; an ambiguous local time
  (fall back) maps to the earlier of the two instants.** Both are "the first UTC instant whose local time is not
  earlier than the given one", so the mapping is monotonic and non-overlapping blocks stay non-overlapping in UTC.
  A block that lies entirely inside the gap (e.g. 02:10–02:50 in Berlin on 29 March) collapses and is skipped.
- **Blocks follow the wall clock**: sleep 01:00–09:00 lasts 7 h on the spring-forward night and 9 h on the
  fall-back night.

### Timeline builder

- **The open activity is a separate input (`OpenActivity`), not an open segment in `Segments`.** The builder
  rejects open segments there; `OpenActivity.FromSegment` converts. Reason: the extension needs the typical
  duration `T`, which the segment does not carry.
- **Unknown `T`: the extension is `fallback × max_factor` long, all of it 🟡.** Using the same factor as for a
  known `T` keeps the "Infinite" preset meaning "until now" in this case too.
- **Every part of the extension that overlaps any schedule block (Sleep, Fixed or Custom) is 🟡**, including
  the part that would otherwise be ⚪ (spec §3.3, last paragraph).
- **Background is "Залипание" 🟡 everywhere outside blocks.** Night-time "Сон" comes only from a Sleep block,
  so a template without one shows Залипание at night.
- **The window is split at `now`; pieces starting at or after `now` have `IsFuture = true`** (forecast from
  stored segments / template / background). The extension never goes past `now`.
- **Adjacent pieces merge only when title, activity, category, status, source and `IsFuture` all match.**

### Period resolution (spec §3.5)

- **All problems are reported at once** (every missing fact, too many unspecified items, overflow, range
  problems), so the bot can queue its questions. Overflow is not computed for a sub-window that still has a
  missing fact.
- **Range anchors** (`с … до …`, local time, `End < Start` → next day) are placed on the earliest date where the
  range fits the window and starts no earlier than the previous anchor's end. A range that sticks out of the window
  by at most one rounding step is clamped to A/B (A is "the last known moment", e.g. 14:03, and users say "с 14").
  A range that does not fit → `RangeOutsideWindow`; one that overlaps or precedes the previous anchor → an extra
  reason `ConflictingRanges` (not in the brief, but needed to keep anchors ordered).
- **A Rest / Unspecified item takes its place in the order** (it can sit in the middle). Rest and Unspecified in
  the same sub-window count together: two of them → `TooManyUnspecified`.
- **Overflow is checked per sub-window**: known minutes > window + one rounding step → `Overflow`; within one
  step → known items are shrunk from the last one backwards. A Rest item in that sub-window then gets zero length
  and is dropped.
- **Rounding**: every interior boundary goes to the nearest multiple of the step in the user's local time (half
  rounds up), then is clamped to stay monotonic and inside [A, B]; A and B never move. **Pieces that end up with
  zero length are dropped** (e.g. "кофе 5 мин" with a 15-minute step) rather than merged into a neighbour — the
  neighbour's content would be a guess. Adjacent unaccounted tails are merged.
- **Unaccounted time is returned as a list (`UnaccountedTails`)**, because anchors can leave several gaps, not
  only a tail at the end.

### Applying changes (`Timeline.Apply`)

- **~~An incoming segment that overlaps the open segment ends it~~ — replaced 2026-11-14 (user decision, spec §3.6).**
  Now the open activity is cut around the incoming segments and **resumes after the last one** when all of these
  hold: the incoming batch ends before `now`, none of it is open, and the first incoming segment starts after the
  open activity's start (otherwise the stated start is contradicted). Spec §3.4 still works because a described
  period that reaches `now` ends the activity. `Apply` therefore takes `nowUtc`.
- **The resumed open segment carries `ElapsedBefore`** — the time of the same activity before the break, summed
  over all breaks. Extension limits become `T·factor − ElapsedBefore` measured from the resumed start, so a lunch
  break does not reset "how long you have been working". Closing a segment drops the value.
- **Parallel activities are a second track (`Segment.IsParallel`, `Timeline.ParallelSegments`)**, not a second
  category on one segment: they have their own boundaries ("обедал 13:00–13:30 за работой" inside a 9-hour work
  segment). The main track keeps all invariants and still sums to 24 h; parallel segments are always closed, may
  overlap anything (including each other), are never cut by main edits, and are built separately
  (`TimelineBuilder.BuildParallel`). An open parallel activity ("сейчас ем за работой") is materialized by the
  Application layer as a closed ⚪ segment of typical length.
- **An incoming open segment covers everything from its start on** (closes the previous open one, removes later
  segments). This is how "start a new activity" works.
- **Adjacent identical stored segments are not merged** — the builder merges for display; storage keeps what was
  said.

## Step 3 — Telegram adapter, onboarding, /blocks, /settings

- **The dialog lives in Application (`ConversationService`), not in the Telegram adapter.** The adapter turns an
  update into `IncomingMessage` (text or button data) and `BotReply` (text, button rows, "replace the pressed
  message") back into Telegram calls. A future iOS client reuses the same service.
- **What the bot waits for is `User.DialogState`, an opaque string owned by Application** (`O:Step` during
  onboarding, `E:Step` for an edit started from a menu — after it the bot returns to that menu). Domain keeps only
  `IsOnboarded`.
- **Blocks are parsed deterministically until the LLM exists (step 5)**: «Работа пн-пт 9-18», «пн, ср, пт»,
  «по субботам», «будни», «каждый день», «с 9 до 18». The parser stays as a fallback afterwards. A block is added
  at once with a "↩️ Убрать" button instead of a separate confirm step — fewer taps, same safety.
- **Category of a block is guessed from its title** (работа → work, учёба/пары → study, тренировка/зал → sport);
  otherwise left empty and asked later (step 7).
- **Sleep days:** a bedtime at or after 12:00 starts on the evening before the wake-up day. "Weekdays" are nights
  before Mon–Fri (so 23:00 sleep starts Sun–Thu), "weekend" nights are before Sat–Sun.
- **Block conflicts are refused with the conflicting block named**, never auto-trimmed.
- **∞ in extension settings is stored as `null`** for both factors (`UserSettings.Extension` maps to
  `ExtensionPolicy`), because SQLite and Postgres disagree on storing infinity.
- **Long polling, not a webhook:** works behind NAT and from a home PC; a webhook can come with the VPS deploy.
  `Telegram:Proxy` covers networks where api.telegram.org is reachable only through a local proxy.
- **Only private chats are served**, and the bot is **closed by default** (#49): with an empty
  `Telegram:AllowedChatIds` every chat gets «Этот бот личный», no user is created, and the chat id is logged so the
  owner can add themselves. `Telegram:AllowAnyone=true` is the explicit open mode. A personal server should not
  hand out accounts and LLM budget to whoever finds the bot.

## Step 4 — pings and recording activities

- **When to ping is a pure Domain rule (`PingSchedule.NextDue`)**: an interval after the later of the last ping
  and the last user message; during an open activity not before `T − ElapsedBefore` from its start; never in the
  past (an overdue ping is checked against `now`); pushed to the end of sleep, quiet hours and — unless
  `PingDuringBlocks` — fixed/custom blocks. `MaxUnansweredPings` unanswered in a row → pause until the user writes.
- **Ping state lives on `User`** (`LastSeenAtUtc`, `LastPingAtUtc`, `LastPingMessageRef`, `UnansweredPings`)
  instead of a `pings` table: the rule needs only these, and the history adds nothing yet. Same for quiet hours:
  one `QuietUntilUtc` instead of `quiet_periods` — `/quiet` is always "from now for N hours".
- **"Всё ещё …" materializes the time so far**: the open segment is closed at `now` (confirmed) and reopened from
  `now` with `ElapsedBefore` — the same mechanism as a break (spec §3.6), so extension and the next ping count the
  total. The display merges the adjacent pieces.
- **"То же самое" is shown only during an open activity** (as "▶️ Да, …"). Without one, "the same" would mean
  "continue the background", which is not an answer worth a button; recent activities are offered instead.
- **Until the LLM exists, any short free text (≤ 60 chars) starts an activity with that name from now**; longer
  text gets an explanation. Every start reply carries "↩️ Отменить" — `/undo` reverts the last journal entry.
- **Activities are matched by name case-insensitively and ignoring «ё»** (unique per user). Typical duration and
  category stay empty for new activities until step 7 asks for them.
- **Times are cut to the minute.** Display and rounding to the grid (spec §3.5) come later.
- **The journal for `/undo` keeps the last 50 changes per user** as JSON (`timeline_changes`); undo refuses when
  the segments of the change are no longer in the timeline.
- **All `DateTime` columns are read back as UTC** (EF convention) — SQLite does not store `Kind`.
- **The ping loop runs once a minute in Host (`PingWorker`)** and only when a client (`INotifier`) is registered;
  a failure for one user does not stop the others.

## Step 5 — LLM intents (#3, #4)

- **One OpenAI-compatible adapter covers ruble aggregators and providers**; the model is a config value.
  Without `Llm:ApiKey` the provider is disabled and the bot keeps working with buttons and short text.
- **The wire format is `{"intents":[...]}` with snake_case types from spec §8**; times are local `"HH:mm"`, days
  are `today` / `yesterday` / `day_before_yesterday` / an English weekday / `YYYY-MM-DD`. The model never
  computes dates or durations.
- **Validation is strict except for categories**: an unknown type, a missing field or a bad time is an error
  (sent back to the model on the one retry); an unknown category is dropped silently and asked later.
- **Days and times are resolved by code (`PastTime`) with one rule: without a day, take the latest past
  occurrence.** At 06:00 «с 19 до 21» and «вечером» mean yesterday, «ночью» the night that just ended; a range
  whose end is not after its start crosses midnight; an end in the future is cut to now; a start in the future is
  an error (plans are iteration 3). Parts of day: morning 06–12, afternoon 12–18, evening 18–24, night 00–06.
- **A period with no start (`до 15`, or nothing named) has `StartUtc = null`** — "since the last known moment";
  applying it (#6) decides what that is. A past day with only a start is an error (`MissingEnd`).
- **The prompt's own examples are a test**: each must pass `IntentParser`, so the contract and the prompt
  cannot drift apart.
- **The eval compares strictly what shapes the timeline** (types and order, day, times, minutes, the way a
  duration is given, `parallel`) and loosely the names (case, «ё», alternatives `A|B`, prefix). Category is
  checked only when the case states it. Two cases are marked disputable in `note` — fix the case, not the bar,
  if a good model keeps answering them differently.
- **A part of day fills a missing bound**: «вчера с 18 … весь вечер» → 18:00–24:00 (found by the eval set).

## Step 5 — applying intents (#6)

- **A described period is a proposal until confirmed.** The whole model answer is stored (`pending_periods`)
  and the layout is recomputed on ✅ with the original "now" — same result, plus any fact learned meanwhile.
  No answer for 10 minutes, or a new message, records it as ⚪ (spec §3.5 p. 7); ✏️ discards it.
- **A missing route fact is the only clarification kept as state**: the bot asks one question, passes it to the
  model as the last bot question, stores the answer as a fact and re-proposes. Other clarifications
  (too many items without duration, overflow, ranges outside the window, a future period) are explained in
  one message and nothing is kept — the user rephrases.
- **"Since the last known moment"** (a period without a start) = start of the open activity, else the end of
  the last segment within 24 h, else the bot asks "from when?".
- **Activities are created only on commit**, so a rejected proposal leaves no stray activities; a category from
  the model fills an activity's empty category but never overrides one.
- **Parallel items**: with own times — by them; otherwise over the neighbouring main item (previous, or the next
  one if the parallel item is first), from its start for the stated minutes or its whole length. An open
  parallel activity ("обедаю за компом") is a closed ⚪ segment of the typical duration, 30 min if unknown.
- **When the LLM is unavailable or answers twice off-contract**, the bot says so and offers the recent-activity
  buttons; with no key the old behaviour stays (short text = new activity).

## Step 5a — past entries (#7, #8)

- **`/day` without the ➕/🟡 extras of spec §5.6 at first**: ➕ came with `/add`; 🟡 resolving is #12/#13.
- **A date like `20.11` within a month ahead is the future (refused), further ahead — last year** (`31.12` in
  November is last December).
- **`/add` line format is `[day] time title`** (or `title time`); without a day — the latest past occurrence,
  like the LLM path. A range over 12 h without a day is refused as ambiguous («с 11 до 2» at 06:00). Lines that
  overlap each other are refused; the rest are recorded as one journal entry, so `/undo` takes them back together.

## Step 7 — questions and facts (#10)

- **The queue holds one kind of question for now — the typical duration of an activity.** Route facts are asked
  by the period proposal itself (#6) and take priority: while it waits for a fact, the queue stays silent.
- **A question is asked once per subject**, right after the bot's reply and only when no other answer is expected
  (`DialogState` empty). Skipping («не знаю») closes it for good; `/facts` can ask again.
- **Only a bare duration counts as an answer** («40», «минут 25», «около часа», «полтора часа», ≤ 20 chars) —
  «закончил в 19» is a normal message, not «19 минут».
- **New activities get an obvious category from their name** (тренировка → sport) on every path, not only `/add`.

## Step 7 — classification (#11)

- **Default rules live in Domain (`Classifier`)**: films/series/walks → mindful rest; Shorts/Reels/TikTok/feeds →
  wasted.short_video; known multiplayer titles (Dota, CS, Valorant, PUBG, …) → wasted.multiplayer; plus obvious
  work/study/sport/household/commute/family/friends words. YouTube and other games return `null` on purpose.
- **A category is asked once per activity, before its duration** (statistics need it more than pings): YouTube —
  rest / study / wasted; a game — single-player (rest) or multiplayer (wasted); anything else — the top-level
  categories. The answer is stored on the activity, so «Dota 2» is asked never and «Elden Ring» once.
- **Per-segment overrides («этот ютуб был учёбой»)** belong to text edits (#14).

## Step 7 — resolving yellow right away (#12)

- **The bot asks about the first 🟡 span of ≥ 15 min in the last 12 hours that it has not offered yet**,
  appended to its reply; adjacent questionable pieces (extension overrun + background) are one span. A single
  marker `yellow_reviewed_until` moves forward when a span is offered, so each span is asked once whatever the
  answer; leftovers are for the evening review (#13).
- **Time before onboarding is never asked about**: the first message after onboarding sets the marker.
- **Questions (yellow first, then the queue) follow only messages that answer the bot** — text, voice, and the
  act:/per:/q: buttons — not commands or menu/navigation buttons, so `/day` and `/settings` stay clean.
- **"Describe" reuses `/add` for that day**; a filled span is one journal entry (`/undo`).

## Step 7 — evening review and morning question (#13)

- **The evening review covers the "review day" — the local date of now − 6 h**, so a review at 00:30 before a
  01:00 sleep sums up the day that just ended. It is sent by the ping loop from «sleep − offset» until 30 minutes
  after the template sleep starts (later the user is asleep), or appended when a sleep activity is started; once
  per review day, not during quiet hours, and it does not count as an unanswered ping.
- **«⚪ Остальное верно» materializes the day's ⚪ pieces as ✅ user segments** (template blocks, extensions of
  finished activities), excluding the extension of an activity that is still running. One journal entry.
- **The morning question is asked once a day**, on the first answering message after a template sleep that ended
  within 12 h, and only if the night is not already recorded and started after registration. «Нет» asks for the
  range («2-10») and records it as the latest past occurrence.
- **Nothing before registration is ever reviewed** (yellow, evening, morning).

## Step 7 — edits by text (#14)

- **An edit replaces the segment whole (`Timeline.Edit`)**: the old segment is removed and the corrected one is
  applied by the usual rules — longer pushes neighbours aside, shorter leaves the freed time empty (background),
  never a stale remainder of the old activity. One journal entry.
- **The target is the latest segment matching all given clues** (day, a time inside it — the latest past
  occurrence, a loosely matched name); without a day, yesterday evening is searched too.
- **Edits apply at once with ↩️**, like other immediate intents, instead of a separate confirmation: the reply
  shows exactly what changed. A category change applies to that segment only (the activity keeps its category).

## Step 8 — images (#15)

- **SkiaSharp 3.119 (not the fresh 4.x)** in a separate adapter project; port `IDayRenderer` in Application, so
  tests use a fake and another client can render differently.
- **The day is a vertical bar sized for a phone (720×1400)**: colour by category key (sleep and commute differ
  within "Necessary"), ✅ solid, ⚪ pale, 🟡 hatched, future grey without labels, a red «now» line, parallel
  activities in a narrow column. Labels only where a piece is tall enough.
- **The text stays**: it becomes the photo caption (or a separate message when longer than 1024 characters) and
  gains «Итого» per top-level category; parallel time is listed separately and is not part of the 24 h.
- **◀ ▶ edit the photo in place** (`editMessageMedia`); if that fails (old message), a new photo is sent.
- **Fonts**: the first existing of DejaVu (Docker installs `fonts-dejavu-core`), Segoe UI, Arial, then any
  system font with Cyrillic.
- **The Dockerfile now restores all adapter projects** (Speech had been missing since #9).

## Web client (#37, #39)

- **The ping interval is any whole number of minutes from 5 to 240** (`UserSettings.MinPingIntervalMin` /
  `MaxPingIntervalMin`, one rule for the bot and the API). Buttons offer 15 · 30 · 45 · 60 · 90 · 120; free text
  accepts anything in range.
- **`User.TelegramChatId` is nullable.** The web client may be the only client, so a user can exist without
  Telegram; the unique index stays (NULLs are distinct in SQLite and Postgres). The notifier and the ping loop skip
  users without a chat (Web Push will serve them, #40).
- **Single-owner mode, owner resolution**: `Web:TelegramChatId` if set (that Telegram user, created when missing);
  otherwise the only user in the database; otherwise the user without Telegram (created when missing). Resolved on
  every authenticated request in Application (`OwnerService`), not cached.
- **The session is a stateless signed cookie** (`expiry.HMACSHA256`, key = SHA-256 of the access key), not
  ASP.NET Data Protection: Data Protection keys would have to be persisted or every container restart would sign the
  owner out. Changing the access key invalidates all sessions. The key is compared in constant time; login is
  limited to 5 attempts a minute per address; every non-GET request needs `X-TimeShift: 1` (CSRF, together with
  `SameSite=Lax`).
- **A day piece maps to exactly one stored segment, and the mapping lives in Application** (`DayBuilder`):
  `TimelineBuilder` merges equal neighbours and has no segment ids, so Application cuts its pieces at stored-segment
  boundaries and looks the segment up by time containment (extension pieces belong to the running segment). The
  domain builder and Telegram's `/day` text are untouched.
- **The API shows the whole day**, not clamped to now: pieces after now come back with `future: true` (a forecast
  from the template); totals count only the past part of the main track.
- **`POST /api/confirm` materialises computed pieces only** (template and background, never stored segments) and
  stays away from time inside the running activity: the extension of a running activity is confirmed through
  `ContinueAsync` (as «still doing it», up to now), and only together with `includeQuestionable` when part of it is
  🟡 — applying a closed segment over a running one would stop it.
- **Updating a schedule block is remove + add with rollback** (the block keeps its id and activity link); sleep
  stays a separate resource.

## Web Push (#40)

- **Notification channels fan out.** `INotificationChannel` (stable `Key`, `CanReachAsync`, `SendAsync`,
  `DeleteAsync`) is implemented by Telegram (`tg`) and Web Push (`wp`); the only `INotifier` is
  `NotificationFanout` in Application. A failing channel is reported (`IFailureReporter`) and never stops the others.
  The stored message ref is `tg:123,wp:…` so deletion finds its channel (a ref without a key is the old Telegram
  format); refs over the 64-character column limit are dropped. Web Push returns no ref: a new notification with the
  same `tag` replaces the previous one. Telegram behaviour is unchanged.
- **A user nobody can reach is skipped by the ping loop** (`CanReachAsync`: a Telegram chat or a push subscription),
  so pings are not counted as unanswered for nobody. `PingWorker` starts when any channel is registered; the push
  channel is registered when the web client is enabled.
- **The notification kind is a hint on the reply** (`BotReply.Notice`: kind, title, short body, `CanAct`), set where
  the ping and the evening review are built, so channels do not parse texts. The evening body is a one-line summary
  (≤ 160 characters), not the day listing.
- **VAPID keys come from `Push:*` or are generated once and kept in the database** (`server_keys`), so a deployment
  needs no setup and survives restarts. P-256, public key as the 65-byte uncompressed point, both base64url.
- **A library for the protocol** (`Lib.Net.Http.WebPush`): VAPID JWT and aes128gcm payload encryption are easy to get
  subtly wrong and are not worth owning; it sits behind `IWebPushSender`, so tests never touch a push service.
  TTL: 1 h for pings (a stale «what are you doing?» is useless), 3 h for the evening review, 5 min for the test;
  urgency high; 404/410 delete the subscription.

## Activity inside a template block (#54)

- **A block makes the extension questionable only when the extension runs into it** (the block starts after the
  open activity started), as spec §3.3 says. An activity named inside a block («созвон» at 14:35 during work) uses
  the usual norm and maximum from its first minute.
- **"Started" means the start of the current open segment.** An activity that began before the block, was
  interrupted inside it and resumed there (`Segment.ResumedAt`) behaves like one started inside the block.

## Three designs stay (#36, 2026-11-17)

- **All three PWA designs are kept** («Лист», «Сетка», «Нить») instead of choosing one (#46 is closed as not
  planned): the look is a user preference, switched in Настройки → Прочее → Вид, stored per device.
- **The price is accepted knowingly:** every new screen or state has to be styled three times. It stays cheap
  only while the rule holds that all logic and markup live in `core/` and a design folder is CSS plus the main
  screen. A feature that cannot be expressed through the core's panels is a reason to extend the core, not a
  design.

## `/week` (#16)

- **The week is the calendar week Mon–Sun in the user's time zone**; the current one is counted up to "now". ◀ ▶
  move by a week, never past the current one and not into weeks that lie wholly before registration.
- **Week numbers are the sum of the `/day` numbers.** Each day is built with the same window as `/day`, categories
  are grouped the same way (sub-categories of «Залипание» are folded into it), so the totals cannot drift apart.
  Days before the registration day are not counted and are drawn empty, like future days.
- **Comparison is with the same part of the previous week** — whole days against whole days, today against the
  same number of hours from midnight a week earlier — so a half-finished week is not compared with a full one.
  It is shown only when the previous week lies wholly after registration. A category that existed only last week
  stays in the list with zero and a negative difference.
- **Shares ✅/⚪/🟡 are whole percents that sum to 100** (largest remainder). Parallel activities are a separate
  line and are not part of the total (spec §3.6).
- **The picture shows numbers, not only strips:** a strip per day, a bar per category with hours and the
  difference, one bar for the status shares. Differences are neutral grey: whether more sleep or less work is
  good is not the bot's call.

## Postgres in tests and CI (#17)

- **One suite, two databases — not a second set of Postgres tests.** Tests take their database from
  `TestDatabase`; the environment variable `TIMESHIFT_TEST_POSTGRES` switches the same tests from SQLite to
  Postgres. A provider difference then shows up in whichever existing test touches it, and a new test covers both
  without extra work. Without the variable nothing needs Postgres or Docker, so the local loop stays as it was.
- **A database per test, created and dropped by the test** (`CREATE DATABASE timeshift_test_<guid>`, then
  `DROP DATABASE … WITH (FORCE)`), with the Postgres migrations applied from scratch each time — the same
  isolation the SQLite file gives, and tests keep running in parallel. Connections to these databases are not
  pooled: a pool per database would leave idle connections and hit the server's `max_connections`.
- **CI runs the suite twice in the one `build` job** (SQLite, then Postgres on a `postgres:17` service
  container, the image of `docker-compose.yml`) instead of a matrix: the required check keeps its name, and the
  second run adds about 20 s (measured locally). The Postgres run uses `--fail-skips on`, so a test that needs Postgres cannot be
  skipped there unnoticed (a misspelt variable fails the job instead of quietly testing SQLite again).
- **No Testcontainers.** A service container in CI and an optional connection string locally do the same job
  without a package and without requiring Docker for `dotnet test`.
- **Persistence tests moved from in-memory SQLite to a temporary file**, the same as the web tests: one helper,
  one kind of SQLite database. The file is deleted after the test (the connection pool is cleared first, which
  the old web-test cleanup did not do on Windows).

## Voice in the PWA (#50)

- **The recording is the request body: `POST /api/messages` with `Content-Type: audio/wav`**, not multipart and not
  base64 inside JSON. One file and nothing else to send; no form parser, no 33 % base64 overhead, and the JSON
  contract of the route stays as it was. The answer has the same shape as for text.
- **One format on the wire — WAV PCM 16-bit mono 16 kHz, made on the device** (`MediaRecorder` → `decodeAudioData`
  → `OfflineAudioContext`). The server needs no FFmpeg and no AAC/WebM decoders; 32 kB per second is acceptable
  for 10–30 s phrases (a 30 s phrase is about 1 MB). The server-side WAV reader is nevertheless tolerant (PCM 16 /
  float 32, any channel count and rate), because it costs little and a second client will not have to match the
  PWA exactly.
- **The port takes "audio" and recognises the format by its signature** (`RIFF` → WAV, otherwise OGG/Opus) instead
  of a format parameter: Telegram and the web call the same `ISpeechToText.TranscribeAsync(Stream)`.
- **What was heard comes back as the first reply («🎙 «…»»)**, exactly as in Telegram — the same
  `ConversationService` code path — rather than as a separate `transcript` field. In the PWA thread the user's own
  message is «🎙 Голосовое · 0:12», then the bot's echo, then the result.
- **Silence and a recognition failure are ordinary replies (`200`)**, not errors: they are answers of the dialog,
  shown in the thread like in Telegram. Request errors are only what the client could have avoided: no model
  (`409`), not a WAV or too long (`422`).
- **The length limit is checked three times, cheaply:** the client stops recording at `meta.speechMaxSec` and cuts
  the WAV to it; the route reads at most the bytes of that many seconds plus one; the adapter counts decoded
  samples (a WAV of another rate could be longer than its size suggests).
- **No microphone without a model:** the button is built only when `meta.speech` is true (and the browser can
  record). The server answers `409` to voice in that state anyway.
- **Hold to talk, release to send, slide away to cancel; a tap only explains.** Holding shorter than 0.6 s sends
  nothing. Keyboard: hold Space / Enter on the button, Escape cancels.
- **One slot in the composer:** the microphone while the field is empty, «Отправить» once there is text — the
  button under the finger never moves and nothing is squeezed on a 320 px screen.
- **The microphone is opened per phrase and released at once** (no indicator left on between phrases). The price
  is a short start-up delay on every press; if it loses the first word on real phones, the alternative is to keep
  the stream while the sheet is open.
- **The demo has the microphone** (it "hears" a fixed phrase) so that the three designs can be reviewed without a
  model; `?demo=1&speech=0` shows the composer of a server without one.

## Dragging boundaries on the timeline (#51)

- **No new API.** Moving an edge is `PATCH /api/entries/{id}` with that edge alone; a new interval is the ordinary
  `POST /api/entries` from the entry sheet. The displacement rule stays where it was (`Timeline.Edit` / `Apply`
  and its one journal step); the PWA has no second copy of it. A Web test pins the contract the gesture relies on:
  one moved edge displaces the neighbour, undo returns both.
- **"Вытеснение" = the dragged entry wins.** Growing an entry over its neighbour cuts the neighbour (a stored one
  is shortened or removed, computed time simply gives way); shrinking it returns the freed time to the template /
  background, it does not stretch the neighbour. The client previews only the dragged entry; what happened to the
  neighbours is shown after the release, by the optimistic patch and then the server's day.
- **One gesture — one edge — one undo step.** The edge is saved on release and the handles go away, so «Отменить»
  in the toast returns exactly the previous bounds. Keeping the handles to move the second edge would make two
  steps and the toast would undo only the last; moving both edges is two long presses (or the sheet).
- **Long press is 450 ms with 10 px of tolerance** — the same as `longPress` in `core/gestures.js`. Until it fires
  the touch belongs to the browser (scrolling, the day swipe); any movement or scroll cancels the press. After it
  fires the page is held still by a non-passive `touchmove` listener, which is the only way to stop a scroll that
  has not started yet on both iOS and Android.
- **A selected entry does not block scrolling.** Only the two handles take touches; the overlay itself lets them
  through, otherwise a long entry (sleep fills the screen) could not be scrolled to reach its other edge. A tap
  outside puts the handles away and does nothing else.
- **Handles sit on opposite sides** (start on the left third, end on the right third): two 44 px targets on the
  same vertical would overlap on any entry shorter than 44 px.
- **Unrecorded time means "no stored entry here"**: background, template blocks and extensions are all computed,
  and a long press on them starts a new entry. A stored entry is never the start of a new one (it gets handles),
  but the dragged interval may run over stored neighbours — the usual displacement applies when it is saved.
- **The new entry is not saved by the drag.** The gesture gives the interval, the entry sheet asks what it was —
  one `POST`, one undo step. A long press without a drag opens the sheet for an hour from the pressed quarter, cut
  to the unrecorded piece (this replaces the long press design B had of its own).
- **Steps come from `roundingMin`**; an edge can also stop exactly at "now" or at the other edge minus one step.
  The future is not reachable: the server rejects it, so the drag stops at "now".
- **Left out:** parallel entries, the end of the running entry (that is «Закончил»), edges lying on another day,
  a keyboard path — all of these stay in the entry sheet.

## Own categories (#52)

- **The table `categories` holds only what the user added.** The standard set stays in code (`DefaultCategories`)
  and is the same for everyone; spec §7 draws one table with `user_id = null` for system rows, but that would mean
  seeding rows that never differ from the code and a second source of truth for them. `CategorySet` (Domain) is the
  user's whole set — standard plus own — and is what names, validates and folds categories everywhere.
- **Still referenced by string key, not by id.** Segments, activities and blocks already store keys, so nothing
  stored had to be rewritten. An own category gets a generated key `u.<10 hex>` once and keeps it through renames
  and moves. It is short on purpose: Telegram button data is 64 bytes and a category question puts the key next to
  a question id (`q:<guid>:<key>`).
- **Own categories are part of the `User` aggregate** (loaded with the user, like the week-template blocks). The
  set is needed wherever a user is — totals, pictures, prompts, validation — and it is small (at most 50), so this
  is cheaper than a repository threaded through every service. The rules live in `User.AddCategory /
  UpdateCategory / RemoveCategory`; the Web adapter and the Telegram dialog only translate.
- **Delete is an archive.** The row stays with `archived_at`; the category leaves every choice (pickers, the
  category question, the LLM contract, validation of `category` in requests) but still resolves by key, so old
  segments keep their name, parent and colour and their time stays in the totals under the same name. Nothing in
  the timeline is rewritten. Its own subcategories are archived with it.
- **What pointed at a deleted category and is not history is cleared**: activities lose it as their default
  category (the bot asks about them again), week-template blocks stay without a category. The assumed (⚪) time
  of such a block on past days therefore loses the category too — it was never stored, it is recomputed from the
  template, exactly as when the block itself is edited.
- **Adding a category with the name of a deleted one brings the old one back** with the same key, so the past and
  the future are counted together again; this is also the "undo" of a delete. Deleted subcategories stay deleted.
  One exception: a deleted category that had subcategories is not revived *as a subcategory* (that would make
  three levels) — a new one is created.
- **Two levels, like the standard set.** A parent is a top-level category, standard or own; a subcategory always
  has its parent's group; a category with subcategories cannot become one. Totals fold subcategories into the
  parent in the bot (`/day` «Итого», `/week`, pictures) — `CategorySet.TotalsKey`; the REST API keeps giving
  totals per key and the PWA groups them itself, as before.
- **Names are unique among the categories that are not deleted**, standard ones included, case and «ё» ignored:
  a name is how a category is picked in text («Работа: Созвоны», an answer to the category question).
- **Standard categories cannot be renamed, hidden or deleted** in this step. `sleep` and `wasted` are used by the
  engine (sleep blocks, the background), and hiding the rest is a separate, smaller wish than adding one's own.
- **No colour column.** Spec §7 lists `color`; nothing lets the user choose one yet. An own category is drawn in
  the colour of its standard parent, otherwise in the colour of its group (the bot's pictures); the PWA colours
  everything by group already.
- **The LLM sees the user's set.** The prompt lists the categories that are not deleted with their keys, and the
  parser accepts only those keys (`IntentContext.Categories`); an unknown key on an activity is still dropped,
  on `classify` it is still an error.
- **`/categories` in Telegram**: the set by group, own ones marked 🔹 and listed as buttons (rename / delete);
  «➕» asks for the group, then the name; «Работа: Созвоны» makes a subcategory. Moving a category to another
  group or parent is in the PWA only — in the chat it is delete and add again.
- **Classification rules as user facts** (spec §4.2 — «YouTube по вечерам — отдых») came in a separate pull
  request; see the next section.

## Classification rules by time of day (#52)

- **A rule is conditioned on the time of day only — morning / day / evening / night. No day-of-week conditions.**
  It is stored as a user fact, applied when recording, and shown and removable in `/facts`. This is the
  maintainer's decision (2026-11-18); everything below is how it was carried out.
- **A rule is (activity, part of the day) → category**, at most one per activity and part. It is tied to the
  activity by id, not by name, so it survives a rename; "YouTube" in a rule means the activity called so, not
  every title that contains the word. The answer to a category question without a condition stays what it was:
  the activity's default category (`activities.category_key`).
- **The parts of the day are fixed and are the ones the text parser already used for «вечером»**: morning
  06:00–12:00, day 12:00–18:00, evening 18:00–24:00, night 00:00–06:00, in the user's time zone. They moved from
  `PastTime` to the Domain (`PartOfDay`, `PartsOfDay`) so there is one definition. The hours are shown wherever a
  rule is created or listed («вечером (18:00–24:00)», «Вечером, 18–24» in the PWA). Not configurable: a setting
  nobody asked for, and "evening" in a rule and in «вечером смотрел сериал» should mean the same hours.
- **The part is decided by the start of the entry.** 17:30–18:30 is "day". An entry is not split at a boundary
  and a running activity does not change category when the clock passes one.
- **Precedence** for the category of a new entry: a category named for that entry (the PWA picker, «этот ютуб был
  учёбой») → the rule for its part of the day → the activity's default category (where the `Classifier` guess of
  spec §4.2 lands when the activity is created). A category the LLM attaches to `start_activity` / a period item
  is still only a suggestion for an activity that has none, so a rule beats it too.
- **One place applies it**: `TrackingService.CategoryAsync` (Application) over `CategoryRules.Resolve` (Domain).
  Every path that records goes through it — the dialog and its buttons, pings, `/add`, the 🟡 review, LLM starts,
  parallel entries, periods and renames, and the client use cases (`EntryService`) — so the adapters know nothing
  about rules. A period gets its rules after `PeriodResolver` has placed the items, because only then the starts
  are known; the proposal the user confirms already shows the result.
- **Existing entries are never re-classified** — not when a rule is added, changed or removed, and not when an
  entry's bounds are edited across a boundary. Changing the title of an entry to another activity takes that
  activity's category by the same order (it is a new statement about what it was). The one exception came later
  (#62, the section below): the running entry that has no category gets one when the bot's question is answered.
- **Stored in `user_facts`, with one schema change.** The table held `key → minutes`; a rule needs a category
  key as its value, and encoding it into the key or into the integer would have been a trick. So `user_facts`
  got a nullable text column `value` and `minutes` became nullable: a fact is either a duration (`route:…`,
  minutes) or a rule (`rule:<activity id>:<part>`, value = category key). The unique index on (user, key) is what
  keeps one rule per activity and part. Migrations `ClassificationRules` for both providers; the down migration
  deletes the rules, the old shape has no room for them.
- **Two ways to create a rule in Telegram, one of them without the LLM.** (1) Under the reply to a category answer
  («Запомнил: «Ютуб» — Учёба.») there is a button «Только утром (06:00–12:00)» for the part of the day the
  activity started in: it turns the answer just given into a rule, clears the default again and forgets the
  question, so at another time of day the bot asks once more — that is how "YouTube: ask" of spec §4.2 becomes a
  set of rules. A plain answer is not changed by this: without the button it is the default, as before. (2) With
  the LLM, `classify` got an optional `when` (`morning | afternoon | evening | night`): «ютуб по вечерам — отдых»
  makes the rule and leaves the default alone; without `when` it is the old "this activity is X".
- **`/facts`** lists an activity's rules under it («↳ вечером (18:00–24:00) — Осознанный отдых») and has a «✖»
  button per rule (the first ten). Removing a rule does not bring the category question back by itself; the
  activity falls back to its default, and if it has none the bot asks when the activity is next started (it asks
  at most once per activity unless the answer was narrowed, as above).
- **A rule that points at a deleted own category is dropped with the category**, like the default category of an
  activity is cleared — not kept as a stale line in `/facts`. `CategoryRules.Match` also ignores a category that
  is not offered any more, so a leftover could never classify anything.
- **In the PWA rules live on the activity page** (Настройки → Занятия → занятие → «По времени суток»): four
  category pickers, «Как обычно» removes the rule. REST: `PUT / DELETE /api/activities/{id}/rules/{part}`, and
  `rules` on every activity (`docs/web-api.md`). The entry form shows, for a picked activity, the category the
  entry will get at its start time and follows the time while the picker is untouched.
- **Not done on purpose:** rules by substring or for a whole category («любые игры ночью»), day-of-week
  conditions (the maintainer's decision above), and a rule when the entry itself is corrected («этот ютуб был
  учёбой» changes that entry only, as spec §4.2 says).

## The category answer reaches the running entry (#62)

- **An answer to the category question is also written to the running entry, as a timeline edit with its own
  `/undo` step — not resolved at read time.** Before this the answer went to the activity (or the rule) and to
  later entries only, so the entry the question was about — typically the very first entry of a new activity —
  stayed in «Без категории». This is the maintainer's decision (2026-11-18), chosen over "fall back to the
  activity's category when totals are read"; everything below is how it was carried out.
- **What is written is what the resolution of the section above gives that entry now** — one code path,
  `TrackingService.CategorizeCurrentAsync` over `CategoryAsync`. After a plain answer that is the chosen category.
  After a rule answer («Только вечером», or «ютуб по вечерам — отдых») it is the rule's category only when the
  entry started in that part of the day; otherwise the entry stays without one and the bot asks again, as before.
- **Only the running entry of that activity, and only when its category is empty.** An entry that already has a
  category — named for it, or taken from a rule or default at its start — is left alone; a stored category does
  not say where it came from, so "explicit" means "not empty". Closed entries are not touched: an earlier entry of
  the activity, or the confirmed part that «Всё ещё занят» cut off before the answer, keep no category (they can
  be corrected by an edit). A question answered while another activity is running changes no entry.
- **One answer is at most one `/undo` step, and it reverts the entry only.** The step is an ordinary edit
  (`Timeline.Edit`: the entry is replaced by a copy with the category, same start, same elapsed time), so the
  journal needed nothing new. `/undo` takes the category off the entry; the answer itself — the activity's
  default or the rule — is a fact, not a timeline change, and stays (it is changed in `/facts` or by a new
  phrase). «Только вечером» pressed after a plain answer adds no second step: the entry already has the category
  and it started in that part of the day. If the category was undone in between, the rule puts it back.
- **The reply says so**: «Запомнил: «Гитара» — Учёба. Текущая запись теперь — Учёба (отменить — /undo).» — an
  edit of the timeline that `/undo` and the «↩️ Отменить» buttons will now hit first should not happen silently.
  Without a changed entry the reply is as before.
- **The same through the LLM, alone or among other intents of the message (#67).** «Дота — залипание» said about
  the running activity is the same answer in other words (it is also how a free-form reply to the question
  arrives), so `classify` — with or without `when` — writes to the running entry too. At first (#62) it did so
  only as the single intent of a message, because a read after another intent of the same message did not see
  that intent's unsaved change; #65 removed that reason, and the maintainer decided (2026-11-18) to lift the
  restriction. How it was carried out:
  - Intents are applied in the model's order, and "the running entry" is the one open when `classify` is reached —
    a message with several intents does what the same intents sent one by one would do. Nothing is postponed to
    the end of the message, so «гитара — учёба, всё, закончил» still reaches the entry before it is closed.
  - «Начал гитару, гитара — это учёба», `start` first: the entry is started without a category and `classify`
    puts it on — two `/undo` steps, the first takes the category off, the second reverts the start. `classify`
    first: the activity already has the category when the entry starts, the entry takes it at its start
    (`CategoryAsync`, as any later entry would) — no edit, no «Текущая запись теперь…» line, one `/undo` step.
    The timeline and the totals are the same in both orders; only the number of undo steps differs.
  - The rules above are unchanged: only the open entry of that activity, only when its category is empty, a rule
    only when the entry started in its part of the day. `classify` about another activity changes no entry.
  - Left as it is, and it does depend on the order: when the activity *already had* a category (an earlier answer,
    or a guess from the name), `start` then `classify` leaves the new entry with the old one (it is not empty),
    while `classify` then `start` gives it the new one. This is what two separate messages do as well, and it was
    so before #67; changing it means letting an answer replace a category on an entry, which #62 does not do.
- **Not done on purpose:** the activity page of the PWA (`PATCH /api/activities/{id}`, the rule pickers) does not
  touch the running entry — that is editing a setting, not answering about what is happening now, and the entry
  form is where the PWA sets a category of an entry; past entries of the activity are not re-classified by an
  answer.

## Reads inside one unit of work (#65)

- **A message stays one save; the repositories answer reads as the database would after it.** The dialog saves
  once, at the end of a message — that is what makes a message atomic, and it stays. The EF repositories used to
  answer reads with a query alone, and a query knows nothing of the unit of work: a row added in it is missing,
  a row removed in it is still returned, a row edited in it is matched by its old values. The fix is in
  Infrastructure, not in Application: no intermediate `SaveChanges`, no change to the ports' signatures. A read
  runs the query only to bring the matching rows into the context and then answers from the context with the
  same condition (`TimelineRepository.SegmentsAsync`, `QuestionRepository.WhereAsync`) — everything that differs
  from the database is tracked there, so stored and unsaved state cannot disagree. Lookups by id go through
  `FindAsync`, which looks in the context first.
- **What was confirmed on a real database before fixing** (tests written first and seen failing on SQLite):
  after an unsaved start "current" was the previous activity; the question about a new activity did not come in
  the reply to the message that started it; an LLM message with several timeline intents did worse than work on
  a stale timeline — it *saved* overlapping entries (three open ones for three starts, two copies of the stopped
  entry for «закончил … начал …»), after which the timeline could not be loaded at all; and six changes saved
  together were not undone in reverse order (`/undo` answered «лента с тех пор поменялась»). One more of the same
  kind showed up in the dialog test and was not in the issue: the question queue did not see a question asked or
  closed in the same message, so the duration question did not follow the category answer in the same reply.
  Not reproduced: changes recorded at the same instant by *different* saves were undone in the right order on
  SQLite even before the fix (the query had no tie-break, so nothing promised it) — the test stays as a guard.
- **Scope of "reads see unsaved changes":** the timeline (`LoadAsync`, `FindOpenAsync`, `FindSegmentAsync`), an
  activity by id and the activity list, the question queue. Facts and the pending period already did. Left on
  saved data on purpose: `RecentAsync` / `LastUsedAsync` — SQL aggregates over entries that only order the
  suggestion buttons; making them exact means recomputing them in memory for every activity touched in the unit
  of work, which the buttons do not need.
- **The order of `/undo` is the order of `Record` calls, kept in the journal time — no new column.** All changes
  of one message come with the same "now", so the journal time alone did not order them. `TimelineRepository`
  now makes it grow strictly per user: a change that is not later than the user's latest one is stamped one
  microsecond after it (a microsecond is what Postgres `timestamptz` keeps; the time is cut to it first, so both
  providers store the same value). The time of a change can therefore be a few microseconds off the clock —
  nothing reads it except the ordering. A sequence column would say the same thing with two migrations and a
  backfill of existing rows; it becomes worth it only if a user's requests ever run concurrently (two requests
  at the very same moment can still read the same latest time — nothing serialises them today).
- **Undoing a change that is not saved yet works on the same rules:** an entry added in the unit of work is
  simply dropped from it, and an entry the unit of work removed from the database is un-removed rather than
  inserted again (entries are immutable, the row is the same).
- **`classify` among several intents (#62) was left unchanged here and lifted in #67.** Its reason there — "the
  stored open entry is read before that change is saved" — stopped holding with this fix; the restriction itself
  was removed as a separate decision (the section above).
- **The `/undo` reply is «Отменил последнее изменение ленты»** instead of «…последнюю запись»: the last step can
  be a category put on an entry (#62) or a «закончил», not an entry.
- **Tests:** the in-memory store of `TimeShift.Application.Tests` applies a change at once and cannot show this
  class of error. Rather than rewrite those tests, the scenarios that depend on unsaved state run through the
  whole host on a real database (`TimeShift.Web.Tests/DialogTests.cs`, `POST /api/messages`, with a scripted
  LLM in `TestApp`) and at repository level in `PersistenceTests` — on SQLite and, in CI, on Postgres.

## Maintainer's decisions on #47 and #48 (2026-11-19)

Both issues waited for a decision because they change the spec. The maintainer answered on 2026-11-19; nothing
is built yet. The quotes are the maintainer's words, the readings under them are the session's and can be
corrected in the issues before the work starts.

- **#47, unknown time.** Decision: «неизвестное время описывается как неизвестное, но базово предлагается
  "залипание"». Reading: the issue's option 1 — the background is «Не записано», has no category, is a separate
  line in the totals and is not part of «Потерянное» — and «Залипание» stays the first thing the bot offers when
  it asks about such a span («Пока тебя не было…», the evening review); until the user answers, the time is
  unknown. Changes spec §3.2 and §5.1 item 3, to be edited in the PR that builds it.
- **#48, pings after unanswered ones.** Decision: «сделать настраиваемо». Reading: the behaviour becomes
  settings (Telegram `/settings` and the PWA settings) instead of one built-in choice — what happens after the
  limit of unanswered pings (stop, as today, or back off: the issue's option 1), pings on/off and hours without
  pings (option 3). The defaults were not named; the session's suggestion is back-off, pings on, no quiet hours.
  Option 4 (one ping per typical duration during an activity) is not part of the decision. Changes spec §5.2,
  to be edited in the PR that builds it.

## Ping settings as built (#48, 2026-11-19)

Built on the reading above (stop or back off, pings on/off, daily quiet hours; option 4 left out). Choices the
decision left open:

- **Defaults are the session's choice**, not the maintainer's: back-off, pings on, no quiet hours. The migration
  gives existing users the same (`after_unanswered_pings = 'BackOff'`, `pings_enabled = true`), so after the update
  pings no longer stop for good after two unanswered. Changing a default is one constant and one migration default.
- **Back-off.** After `max_unanswered` unanswered pings in a row each next interval doubles: the first ping
  past the limit comes after 2×, then 4×, … (30 → 60 → 120 → 240 min with the defaults), capped at 4 h — or at the
  interval itself when it is longer than 4 h, so back-off never makes pings more frequent. The count of
  unanswered pings already resets on any message, so the first answer restores the interval. `max_unanswered`
  keeps its meaning (how many before the change) and its 1–10 range.
- **Quiet hours** are one daily window, the same every day, stored as two local times (`quiet_start`,
  `quiet_end`, both null — none) and applied in the user's time zone like the week template; across midnight when
  the end is before the start. A ping that falls into them comes at their end, the same as with sleep and blocks.
  They are separate from `/quiet` (a one-off pause until a moment), which stays as it was.
- **What they do not touch.** Pings off and quiet hours concern the «Чем занят?» pings only. The evening review
  (spec §5.4) has its own setting and still comes; a user who wants silence there uses `/quiet` or a later
  evening review offset.
- **Telegram `/settings`** in the existing style: two toggle buttons («🔔 Пинги: вкл/выкл», «Дальше: реже/пауза»)
  and «🤫 Тихие часы», which asks for a range like the sleep question («22-8», or «Без тихих часов»). The menu
  text shows all three. Reworking the menu is #53.
- **PWA** «Напоминания»: a switch «Спрашивать, чем я занят» in «Интервал», a daily quiet-hours switch with two
  time fields under the `/quiet` presets in «Тишина», and a choice «Спрашивать реже / Замолчать до моего ответа»
  next to the unanswered count. The menu shows «Выключены» when pings are off.

## Unknown time as built (#47, 2026-11-19)

Built on the reading above (the issue's option 1, «Залипание» offered first). Choices the decision left open:

- **What is unknown.** A main-track piece whose source is the background (`TimelinePiece.IsUnknown`): title
  «Не записано», no category, still 🟡. Template blocks and the running activity's overrun are not unknown — they
  are what the user said or planned, only unconfirmed. A future background piece is free time, shown as
  «Свободно» (the `/day` text, the PWA).
- **Not «Без категории».** An entry without a category is something the user recorded; unknown time is not.
  So unknown time is never folded into the `null` category: the bot shows a separate line «Не записано», `WeekStats`
  a last row with `IsUnknown`, REST a separate number.
- **REST shape.** `unknownMin` next to `status` in the day, in each summary day and in the range; `totals` no longer
  include it (`totals` + `unknownMin` = the past part). A field of its own rather than a `totals` row with a flag,
  so a client that reads only `totals` cannot mistake it for «not classified». `status.questionableMin` still
  includes it.
- **Last line, whatever its size.** In `/week`, the PNG and the PWA totals «Не записано» goes after the categories
  (and after the groups), so the categories read as what is known.
- **Confirming «as it is».** `POST /api/confirm` with `includeQuestionable` (the PWA's «Ничего особенного», whose
  hint already said «запишет это время как залипание») records a background piece as «Залипание» / `wasted` — what
  the bot offers first. The evening «⚪ Остальное верно» confirms only ⚪ and never touches unknown time.
- **Bot question.** «Пока тебя не было…» puts «Залипание» first (it was last, as «Залипание, так и было»; «так и
  было» no longer fits, since the bot did not say it was).
- **Colour.** Unknown time is a neutral light grey in the `/day` and `/week` pictures, not the orange of «Потерянное».

## Telegram /settings and /blocks as built (#53, 2026-11-19)

- **Settings values.** A number is a row «− value +» (`set:<name>:-` / `set:<name>:+`; the middle button only
  redraws the menu, «−» or «+» disappears at the end of the range): unanswered pings step by 1 within 1–10,
  the evening review by 15 min within 0–180 (a value set elsewhere, say 37, goes to the next multiple: 45 / 30). A
  short fixed list is a choice with ✓, like the ping interval: rounding 1 / 5 / 10 / 15 min (`set:rounding:<n>`;
  the old cycle had no 1). Toggles stay toggles. Buttons of an old menu (`set:unanswered` without an argument)
  change nothing and redraw the menu.
- **Deleting a block** in `/blocks` puts it in a one-place trash: the menu is redrawn with «Убрал: …» and a first
  button «↩️ Вернуть» (`rb:<id>`). The block keeps its row in `schedule_blocks` with `trashed = true` (migration
  `TrashedBlocks` for both providers) and is out of `User.Blocks` and the template everywhere. Only the last
  removed block is kept: removing another deletes the previous one for good, so «Вернуть» on an older message
  answers «уже не вернуть». A block added into the freed time wins: «Вернуть» then says what it overlaps.
  Why a column: callback data is 64 bytes and `dialog_state` 64 characters — neither holds a block with its title
  and category. The REST `DELETE /schedule/blocks/{id}` still deletes for good (the PWA has its own confirmation).
- **Editing a block by text.** A block typed again («Работа пн-пт 10-19» in «➕ Работа / учёба» or in onboarding)
  replaces the blocks **with the same title (case-insensitive) that it overlaps**, keeping their category; the
  answer says «Заменил: old → new». A same-titled block in other hours is added beside them, because onboarding
  itself suggests two blocks of one name («Учёба пн, ср, пт 9:30-15», «Учёба по субботам 10-13»). Moving a block
  to hours that do not overlap the old ones therefore adds a second one; the user removes the old one with 🗑. A
  replacement has no «Вернуть» (onboarding's «↩️ Убрать» removes the new block only).
- **Left out** (named in the issue, not in «Что сделать»): time zones beyond the eight buttons (any IANA name can
  still be typed) and a finer sleep step (any time can be typed: «23:30-7:30»).
