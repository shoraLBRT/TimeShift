# Roadmap

Order of work for the MVP (milestone **MVP**, epic [#20](https://github.com/shoraLBRT/TimeShift/issues/20)).
Take the first open issue in this list unless the state file says otherwise. The spec is `docs/spec.md`;
plan table in §11.

| # | Stage | Issue | Notes |
|---|---|---|---|
| 1 | 5 · LLM | [#3](https://github.com/shoraLBRT/TimeShift/issues/3) LLM port and OpenAI-compatible adapter | no key needed: tests on recorded responses |
| 2 | 5 · LLM | [#4](https://github.com/shoraLBRT/TimeShift/issues/4) Intent contract: schema, prompt, validation | dates are mapped by code |
| 3 | 5 · LLM | [#5](https://github.com/shoraLBRT/TimeShift/issues/5) Test set of 30–50 phrases and eval run | live run costs money — ask first |
| 4 | 5 · LLM | [#6](https://github.com/shoraLBRT/TimeShift/issues/6) Apply intents: period proposal and confirm buttons | |
| 5 | 5a · Past entries | [#7](https://github.com/shoraLBRT/TimeShift/issues/7) `/day` for another day | |
| 6 | 5a · Past entries | [#8](https://github.com/shoraLBRT/TimeShift/issues/8) `/add` without LLM | |
| 7 | 5 · LLM | [#2](https://github.com/shoraLBRT/TimeShift/issues/2) Choose the LLM provider and model | **blocked:** needs the maintainer's aggregator key |
| 8 | 6 · Voice | [#9](https://github.com/shoraLBRT/TimeShift/issues/9) Voice messages with local whisper.cpp | |
| 9 | 7 · Review & facts | [#10](https://github.com/shoraLBRT/TimeShift/issues/10) Question queue and facts | |
| 10 | 7 · Review & facts | [#11](https://github.com/shoraLBRT/TimeShift/issues/11) Classification rules | |
| 11 | 7 · Review & facts | [#12](https://github.com/shoraLBRT/TimeShift/issues/12) Resolve yellow right after a gap | |
| 12 | 7 · Review & facts | [#13](https://github.com/shoraLBRT/TimeShift/issues/13) Evening review and morning sleep question | |
| 13 | 7 · Review & facts | [#14](https://github.com/shoraLBRT/TimeShift/issues/14) Edit segments by text | |
| 14 | 8 · Images | [#15](https://github.com/shoraLBRT/TimeShift/issues/15) `/day` as an image | |
| 15 | 8 · Images | [#16](https://github.com/shoraLBRT/TimeShift/issues/16) `/week` image and stats | |
| 16 | 9 · Deploy | [#17](https://github.com/shoraLBRT/TimeShift/issues/17) Real Postgres in CI | |
| 17 | 9 · Deploy | [#18](https://github.com/shoraLBRT/TimeShift/issues/18) Deploy to a VPS abroad | needs the maintainer to choose and pay for the VPS |
| 18 | 9 · Deploy | [#19](https://github.com/shoraLBRT/TimeShift/issues/19) Logs, DB backups, monitoring | |

**MVP exit criterion:** the bot runs on the VPS for a week without manual intervention; no unresolved 🟡 is
left at the end of a day; answering a ping takes one button or one phrase (voice and past entries included);
`/day` and `/week` show where the time went.

## PWA — own mobile client (milestone **PWA**, epic [#36](https://github.com/shoraLBRT/TimeShift/issues/36))

Pulled forward from iteration 3: Telegram is unreliable in Russia (spec §10.1), and an own client lets the
server be hosted there. The REST contract is `docs/web-api.md`; the client is `src/TimeShift.Host/wwwroot`.

| # | Issue | Notes |
|---|---|---|
| 1 | [#37](https://github.com/shoraLBRT/TimeShift/issues/37) Ping interval 5–240 minutes | |
| 2 | [#38](https://github.com/shoraLBRT/TimeShift/issues/38) Stale bot replies | |
| 3 | [#39](https://github.com/shoraLBRT/TimeShift/issues/39) REST API and the user without Telegram | |
| 4 | [#40](https://github.com/shoraLBRT/TimeShift/issues/40) Web Push next to Telegram | |
| 5 | [#41](https://github.com/shoraLBRT/TimeShift/issues/41) PWA core | shared by the three designs |
| 6 | [#42](https://github.com/shoraLBRT/TimeShift/issues/42) · [#43](https://github.com/shoraLBRT/TimeShift/issues/43) · [#44](https://github.com/shoraLBRT/TimeShift/issues/44) Design MVPs A «Лист», B «Сетка», C «Нить» | equal in function; all three stay, switched in the settings |
| 7 | [#45](https://github.com/shoraLBRT/TimeShift/issues/45) Deploy with HTTPS, guide for a VPS in Russia | |
| 8 | [#50](https://github.com/shoraLBRT/TimeShift/issues/50) Voice in the PWA | built; stays open only for the check on an iPhone and Android |
| 9 | [#51](https://github.com/shoraLBRT/TimeShift/issues/51) Drag the edges of an entry | built; stays open only for the check with a finger on an iPhone and Android |
| 10 | [#52](https://github.com/shoraLBRT/TimeShift/issues/52) User-defined categories | built: own categories (table, REST, PWA settings, `/categories`) and classification rules by time of day as user facts (`/facts`, the activity page in the PWA) |

Found while reviewing the current behaviour, decided by the maintainer on 2026-11-19 (`docs/decisions.md`), not
built yet:
[#47](https://github.com/shoraLBRT/TimeShift/issues/47) (unknown time is shown as unknown, «Залипание» is what
the bot offers first),
[#48](https://github.com/shoraLBRT/TimeShift/issues/48) (what pings do after unanswered ones, on/off and quiet
hours become settings).
Also [#49](https://github.com/shoraLBRT/TimeShift/issues/49) (the bot is open to everyone by default; PR open) and
[#53](https://github.com/shoraLBRT/TimeShift/issues/53) (Telegram settings UX).

After the MVP: iteration 2 (iPhone Shortcuts for app usage, Telegram Mini App, per-user LLM provider) and
iteration 3 (planning, calendar export) — spec §11 "Дальше".
