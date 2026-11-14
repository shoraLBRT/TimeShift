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
- **Only private chats are served**; `Telegram:AllowedChatIds` can close the bot to everyone else.

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

