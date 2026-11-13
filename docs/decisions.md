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

- **An incoming segment that overlaps the open segment ends it at the start of the earliest overlapping incoming
  segment; nothing of the open activity continues after the incoming ones.** In spec §3.4 the description of
  14:00–17:00 must replace the open "Тренировка", not leave it running from 17:00. If the user is in fact still
  doing it, the next ping restarts it.
- **An incoming open segment covers everything from its start on** (closes the previous open one, removes later
  segments). This is how "start a new activity" works.
- **Adjacent identical stored segments are not merged** — the builder merges for display; storage keeps what was
  said.
