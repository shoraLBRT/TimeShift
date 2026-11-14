using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Facts;
using TimeShift.Domain.Periods;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Llm;

/// <summary>
/// Применение намерений из текста (spec §5.3, §3.4–3.6): период — предложением с подтверждением; нехватка факта —
/// один вопрос; начать, закончить, тихие часы, факты и категории — сразу. Модель только извлекает, всё считает код.
/// </summary>
public sealed partial class IntentApplier(
    ILlmProvider llm,
    IntentExtractor extractor,
    TrackingService tracking,
    IActivityRepository activities,
    IFactRepository facts,
    IPendingPeriodRepository pendings,
    IMessageLog log)
{
    /// <summary>Без ответа на предложение оно записывается как ⚪ через это время (spec §3.5 п. 7).</summary>
    public static readonly TimeSpan ProposalTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Длительность параллельного занятия «сейчас», если обычная неизвестна (spec §3.6).</summary>
    private static readonly TimeSpan DefaultParallelDuration = TimeSpan.FromMinutes(30);

    /// <summary>Насколько далеко назад искать «последний известный момент» для периода без начала.</summary>
    private static readonly TimeSpan LastKnownLookback = TimeSpan.FromHours(24);

    public bool IsEnabled => llm.IsEnabled;

    /// <summary>Разбор периода ждёт ответа о факте — его вопрос важнее очереди (spec §5.5: один вопрос за раз).</summary>
    public async Task<bool> IsWaitingForFactAsync(User user, CancellationToken ct) =>
        await pendings.GetAsync(user.Id, ct) is { IsWaitingForFact: true };

    /// <summary>Текст → намерения → действия. <c>null</c> — LLM выключен (вызывающий работает без него).</summary>
    public async Task<IReadOnlyList<BotReply>?> HandleTextAsync(User user, string text, DateTime now, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var replies = new List<BotReply>();
        var pending = await pendings.GetAsync(user.Id, ct);
        if (pending is { IsWaitingForFact: false })
        {
            // Пользователь пишет дальше, не нажав «Верно», — предложение записывается как ⚪.
            await CommitAsync(user, pending, SegmentStatus.Assumed, now, ct);
            pending = null;
        }

        var extraction = await extractor.ExtractAsync(text, await ContextAsync(user, now, pending?.Question, ct), ct);
        log.Add(new MessageLogEntry
        {
            UserId = user.Id,
            AtUtc = now,
            Text = text,
            Status = extraction.Status.ToString(),
            Response = extraction.RawResponses.LastOrDefault() ?? string.Join("; ", extraction.Errors),
            PromptTokens = extraction.PromptTokens,
            CompletionTokens = extraction.CompletionTokens,
        });

        switch (extraction.Status)
        {
            case ExtractionStatus.Disabled:
                return null;
            case ExtractionStatus.Unavailable:
                replies.Add(await NotUnderstoodAsync(user, "Не получилось разобрать — сервис распознавания сейчас недоступен.", ct));
                return replies;
            case ExtractionStatus.Invalid:
                replies.Add(await NotUnderstoodAsync(user, "Не понял 🤔 Скажи иначе или выбери:", ct));
                return replies;
        }

        var response = extraction.RawResponses[^1];
        for (var i = 0; i < extraction.Intents.Count; i++)
        {
            replies.AddRange(await ApplyAsync(user, extraction.Intents[i], text, response, i, pending, now, ct));
        }

        return replies;
    }

    /// <summary>Кнопки предложения: <c>per:ok</c>, <c>per:edit</c>. <c>null</c> — не наша кнопка.</summary>
    public async Task<IReadOnlyList<BotReply>?> OnButtonAsync(User user, string data, DateTime now, CancellationToken ct)
    {
        if (data is not ("per:ok" or "per:edit"))
        {
            return null;
        }

        var pending = await pendings.GetAsync(user.Id, ct);
        if (pending is null or { IsWaitingForFact: true })
        {
            return [new BotReply("Это предложение уже неактуально.", [], ReplacesSource: true)];
        }

        if (data == "per:edit")
        {
            await pendings.RemoveAsync(user.Id, ct);
            return [new BotReply("Хорошо, не записываю. Напиши, как было на самом деле.", [], ReplacesSource: true)];
        }

        var segments = await CommitAsync(user, pending, SegmentStatus.Confirmed, now, ct);
        return
        [
            new BotReply(
                "✅ Записал:\n" + Lines(segments, user.TimeZoneInfo),
                [[new ReplyButton("↩️ Отменить", "tl:undo")]],
                ReplacesSource: true),
        ];
    }

    /// <summary>Предложение без ответа дольше <see cref="ProposalTimeout"/> записывается как ⚪ (из фонового цикла).</summary>
    public async Task<bool> FinalizeExpiredAsync(User user, DateTime now, CancellationToken ct)
    {
        var pending = await pendings.GetAsync(user.Id, ct);
        if (pending is null || pending.IsWaitingForFact || now - pending.CreatedAtUtc < ProposalTimeout)
        {
            return false;
        }

        await CommitAsync(user, pending, SegmentStatus.Assumed, now, ct);
        return true;
    }

    private async Task<IReadOnlyList<BotReply>> ApplyAsync(
        User user, Intent intent, string text, string response, int index, PendingPeriod? pending, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        switch (intent)
        {
            case StartActivityIntent start:
                return [await StartAsync(user, start, now, ct)];

            case StopActivityIntent stop:
            {
                var at = stop.At is { } time ? PastTime.ResolveInstant(stop.Day, time, now, tz) : null;
                if (stop.At is not null && at is null)
                {
                    return [BotReply.Plain("Это время ещё не наступило — закончить можно только в прошлом.")];
                }

                var stopped = await tracking.StopAsync(user, now, ct, at);
                return stopped is null
                    ? [BotReply.Plain("Сейчас нет начатого занятия.")]
                    : [new BotReply(TrackingReplies.Stopped(stopped, tz), [[new ReplyButton("↩️ Отменить", "tl:undo")]])];
            }

            case DescribePeriodIntent period:
                return [await ProposeAsync(user, period, text, response, index, now, ct)];

            case AnswerIntent answer when pending is { IsWaitingForFact: true }:
            {
                if (ParseMinutes(answer.Value) is not { } minutes)
                {
                    return [BotReply.Plain($"Не понял, сколько минут. {pending.Question}")];
                }

                await facts.SetAsync(user.Id, pending.FactKey!, minutes, ct);
                return [BotReply.Plain($"Запомнил: {FactName(pending.FactKey!)} — {minutes} мин."), await RetryAsync(user, pending, ct)];
            }

            case AnswerIntent:
                return [BotReply.Plain("Не понял, к чему это ответ. Напиши, чем занят или что было.")];

            case ClassifyIntent classify:
            {
                var activity = await tracking.ResolveActivityAsync(user, classify.Activity, ct);
                activity.CategoryKey = classify.Category;
                return [BotReply.Plain($"Запомнил: «{activity.Name}» — {CategoryName(classify.Category)}.")];
            }

            case SetFactIntent fact:
            {
                if (fact.Route is { } route)
                {
                    var key = UserFact.RouteKey(route.From, route.To);
                    await facts.SetAsync(user.Id, key, fact.Minutes, ct);
                    var saved = BotReply.Plain($"Запомнил: {FactName(key)} — {fact.Minutes} мин.");
                    return pending is { IsWaitingForFact: true } p && p.FactKey == key ? [saved, await RetryAsync(user, p, ct)] : [saved];
                }

                var activity = await tracking.ResolveActivityAsync(user, fact.Activity!, ct);
                activity.TypicalDuration = TimeSpan.FromMinutes(fact.Minutes);
                return [BotReply.Plain($"Запомнил: «{activity.Name}» обычно длится {TrackingReplies.Duration(activity.TypicalDuration.Value)}.")];
            }

            case QuietIntent quiet:
            {
                var until = quiet.Minutes is { } m
                    ? now.AddMinutes(m)
                    : NextOccurrence(quiet.Until!.Value, now, tz);
                user.SetQuietUntil(TrackingService.Minute(until));
                return [BotReply.Plain($"🔕 Не беспокою до {TrackingReplies.Clock(until, tz)}. Снять: /quiet off")];
            }

            case EditSegmentIntent edit:
                return [await EditAsync(user, edit, now, ct)];

            case UnclearIntent unclear:
                return [await NotUnderstoodAsync(user, unclear.Question, ct)];

            default:
                return [];
        }
    }

    private async Task<BotReply> StartAsync(User user, StartActivityIntent start, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        DateTime? at = null;
        if (start.At is { } time)
        {
            at = PastTime.ResolveInstant(start.Day, time, now, tz);
            if (at is null)
            {
                return BotReply.Plain("Это время ещё не наступило — планы я научусь записывать позже.");
            }
        }

        var activity = await tracking.ResolveActivityAsync(user, start.Activity, ct);
        activity.CategoryKey ??= start.Category;

        if (start.Parallel)
        {
            // «Ем за работой»: параллельный отрезок на обычную длительность, ⚪ — поправить можно (spec §3.6).
            var from = TrackingService.Minute(at ?? now);
            var length = activity.TypicalDuration ?? DefaultParallelDuration;
            var segment = new Segment(
                from, from + length, activity.Name, SegmentStatus.Assumed, SegmentSource.User, activity.Id, activity.CategoryKey, isParallel: true);
            await tracking.ApplyAsync(user, [segment], now, ct);
            return new BotReply(
                $"➕ Параллельно: «{activity.Name}» {TrackingReplies.Clock(segment.StartUtc, tz)}–{TrackingReplies.Clock(segment.EndUtc!.Value, tz)} ⚪, " +
                "основное занятие идёт дальше.",
                [[new ReplyButton("↩️ Отменить", "tl:undo")]]);
        }

        var started = await tracking.StartAsync(user, activity, now, ct, at);
        return TrackingReplies.Started(started, tz);
    }

    // ---- Правка --------------------------------------------------------------------------------------------

    /// <summary>
    /// «Тренировка была до 15:30», «тот ютуб в 14 был учёбой»: найти отрезок по дню, времени внутри него и названию
    /// (последний подходящий), поменять границы / название / категорию и записать как ✅.
    /// </summary>
    private async Task<BotReply> EditAsync(User user, EditSegmentIntent edit, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(now, tz));
        var day = edit.Target.Day is { } d ? PastTime.ResolveDate(d, today) : today;
        var dayStart = LocalTime.ToUtc(day.ToDateTime(TimeOnly.MinValue), tz);
        var dayEnd = LocalTime.ToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

        // Без дня ищем и во вчерашнем вечере: в 6 утра «тренировка была до 22» — про вчера.
        var searchFrom = edit.Target.Day is null ? dayStart - TimeSpan.FromHours(12) : dayStart;
        var timeline = await tracking.LoadAsync(user, searchFrom, ct);
        var at = edit.Target.At is { } t ? PastTime.ResolveInstant(edit.Target.Day, t, now, tz) : null;
        var target = timeline.Segments.Concat(timeline.ParallelSegments)
            .Where(s => s.StartUtc < dayEnd && (s.EndUtc ?? now) > searchFrom)
            .Where(s => at is null || (s.StartUtc <= at && at < (s.EndUtc ?? now)))
            .Where(s => edit.Target.Activity is not { } name || SameName(name, s.Title))
            .OrderBy(s => s.StartUtc)
            .LastOrDefault();
        if (target is null || (edit.Target.At is not null && at is null))
        {
            return BotReply.Plain("Не нашёл такой записи. Посмотри /day и напиши точнее — с временем, например «в 14 был не обед, а созвон».");
        }

        var window = (From: target.StartUtc - TimeSpan.FromHours(12), To: (target.EndUtc ?? now) + TimeSpan.FromHours(12));
        var start = edit.NewStart is { } ns ? PastTime.ResolveInWindow(ns, window.From, window.To, tz) ?? target.StartUtc : target.StartUtc;
        var end = edit.NewEnd is { } ne ? PastTime.ResolveInWindow(ne, start, window.To, tz) : target.EndUtc;
        if (end is { } e && e > now)
        {
            return BotReply.Plain("Конец получается в будущем — так поправить нельзя.");
        }

        if (end is { } closed && closed <= start)
        {
            return BotReply.Plain("Начало получается позже конца — проверь время.");
        }

        var title = target.Title;
        var activityId = target.ActivityId;
        var category = target.CategoryKey;
        if (edit.NewActivity is { } rename)
        {
            var activity = await tracking.ResolveActivityAsync(user, rename, ct);
            (title, activityId, category) = (activity.Name, activity.Id, activity.CategoryKey ?? category);
        }

        if (edit.NewCategory is { } newCategory)
        {
            category = newCategory;
        }

        var replacement = new Segment(
            TrackingService.Minute(start), end is { } en ? TrackingService.Minute(en) : null, title, SegmentStatus.Confirmed, SegmentSource.User,
            activityId, category, target.IsOpen && end is null ? target.ElapsedBefore : default, target.IsParallel);
        if (await tracking.EditAsync(user, target, replacement, now, ct) is null)
        {
            return BotReply.Plain("Эта запись уже изменилась — посмотри /day.");
        }

        var span = $"{TrackingReplies.Clock(replacement.StartUtc, tz)}–{(replacement.EndUtc is { } re ? TrackingReplies.Clock(re, tz) : "…")}";
        var categoryNote = edit.NewCategory is { } c ? $" ({CategoryName(c)})" : string.Empty;
        return new BotReply($"✏️ Исправил: {span} {replacement.Title}{categoryNote}.", [[new ReplyButton("↩️ Отменить", "tl:undo")]]);
    }

    private static bool SameName(string wanted, string actual)
    {
        static string N(string x) => x.Trim().ToLowerInvariant().Replace('ё', 'е');
        var (w, a) = (N(wanted), N(actual));
        return w == a || a.Contains(w, StringComparison.Ordinal) || w.Contains(a, StringComparison.Ordinal)
               || (w.Length >= 4 && a.Length >= 4 && w[..4] == a[..4]);
    }

    // ---- Период --------------------------------------------------------------------------------------------

    private async Task<BotReply> ProposeAsync(
        User user, DescribePeriodIntent period, string text, string response, int index, DateTime now, CancellationToken ct)
    {
        var plan = await PlanAsync(user, period, now, createActivities: false, ct);
        switch (plan)
        {
            case Plan.Failed failed:
                await pendings.RemoveAsync(user.Id, ct);
                return BotReply.Plain(failed.Message);

            case Plan.MissingFact missing:
            {
                var question = FactQuestion(missing.Key);
                await pendings.SetAsync(new PendingPeriod(user.Id, now, text, response, index, missing.Key, question), ct);
                return BotReply.Plain(question);
            }

            case Plan.Ready ready:
                await pendings.SetAsync(new PendingPeriod(user.Id, now, text, response, index, null, null), ct);
                return new BotReply(
                    "Записать так?\n" + Lines(ready.Segments, user.TimeZoneInfo) + Tails(ready.Tails, user.TimeZoneInfo),
                    [[new ReplyButton("✅ Верно", "per:ok"), new ReplyButton("✏️ Поправить", "per:edit")]]);

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>После ответа на вопрос — пересчитать период с новым фактом.</summary>
    private async Task<BotReply> RetryAsync(User user, PendingPeriod pending, CancellationToken ct)
    {
        var period = PeriodOf(pending);
        return await ProposeAsync(user, period, pending.Text, pending.ResponseJson, pending.IntentIndex, pending.CreatedAtUtc, ct);
    }

    private async Task<IReadOnlyList<Segment>> CommitAsync(User user, PendingPeriod pending, SegmentStatus status, DateTime now, CancellationToken ct)
    {
        await pendings.RemoveAsync(user.Id, ct);
        if (await PlanAsync(user, PeriodOf(pending), pending.CreatedAtUtc, createActivities: true, ct) is not Plan.Ready ready)
        {
            return [];
        }

        var segments = status == SegmentStatus.Confirmed
            ? ready.Segments
            : ready.Segments.Select(s => new Segment(s.StartUtc, s.EndUtc, s.Title, status, s.Source, s.ActivityId, s.CategoryKey, isParallel: s.IsParallel)).ToList();
        await tracking.ApplyAsync(user, segments, now, ct);
        return segments;
    }

    private static DescribePeriodIntent PeriodOf(PendingPeriod pending) =>
        (DescribePeriodIntent)IntentParser.Parse(pending.ResponseJson).Intents[pending.IntentIndex];

    private abstract record Plan
    {
        public sealed record Ready(IReadOnlyList<Segment> Segments, IReadOnlyList<TimeRange> Tails) : Plan;

        public sealed record MissingFact(string Key) : Plan;

        public sealed record Failed(string Message) : Plan;
    }

    /// <summary>Окно → пункты → PeriodResolver → основные отрезки + параллельные.</summary>
    private async Task<Plan> PlanAsync(User user, DescribePeriodIntent period, DateTime now, bool createActivities, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        var result = PastTime.ResolveWindow(period.Day, period.From, period.To, period.PartOfDay, now, tz);
        if (result.Error is { } error)
        {
            return new Plan.Failed(error == WindowError.Future
                ? "Это в будущем — записываю только то, что уже было. Планы появятся позже."
                : "До скольки это было? Напиши, например, «вчера с 19 до 21 был в зале».");
        }

        var window = result.Window!;
        var end = TrackingService.Minute(window.EndUtc);
        var start = window.StartUtc is { } s ? TrackingService.Minute(s) : await LastKnownAsync(user, end, ct);
        if (start is null || start >= end)
        {
            return new Plan.Failed("С какого времени это было? Напиши, например, «с 14 до 17 …».");
        }

        var range = new TimeRange(start.Value, end);
        var main = new List<(PeriodItemIntent Item, int Index)>();
        var parallel = new List<(PeriodItemIntent Item, int Index)>();
        for (var i = 0; i < period.Items.Count; i++)
        {
            (period.Items[i].Parallel ? parallel : main).Add((period.Items[i], i));
        }

        if (main.Count == 0)
        {
            return new Plan.Failed("Не понял, что было основным занятием. Напиши подробнее.");
        }

        var items = new List<PeriodItem>();
        var resolved = new Dictionary<int, Activity?>();
        foreach (var (item, i) in period.Items.Select((x, i) => (x, i)))
        {
            resolved[i] = createActivities
                ? await tracking.ResolveActivityAsync(user, item.Activity, ct)
                : await activities.FindByNameAsync(user.Id, item.Activity, ct);
            if (createActivities && resolved[i] is { CategoryKey: null } fresh && item.Category is not null)
            {
                fresh.CategoryKey = item.Category;
            }
        }

        foreach (var (item, i) in main)
        {
            var activity = resolved[i];
            items.Add(new PeriodItem(
                activity?.Name ?? Capitalize(item.Activity),
                Spec(item),
                activity?.CategoryKey ?? item.Category,
                activity?.Id));
        }

        var resolution = PeriodResolver.Resolve(new PeriodRequest
        {
            Window = range,
            Items = items,
            Facts = await facts.GetAsync(user.Id, ct),
            TimeZone = tz,
            RoundingMinutes = user.Settings.RoundingMin,
        });

        switch (resolution)
        {
            case NeedsClarification clarification:
                return clarification.Reasons.OfType<ClarificationReason.MissingFact>().FirstOrDefault() is { } missing
                    ? new Plan.MissingFact(missing.Key)
                    : new Plan.Failed(Explain(clarification.Reasons, main.Select(m => m.Item).ToList(), range, tz));

            case PeriodProposal proposal:
            {
                var segments = proposal.Segments.ToList();
                segments.AddRange(Parallel(main, parallel, proposal.Segments, resolved, range, tz));
                return new Plan.Ready(segments, proposal.UnaccountedTails);
            }

            default:
                throw new InvalidOperationException();
        }
    }

    private static DurationSpec Spec(PeriodItemIntent item) => item switch
    {
        { Minutes: { } m } => new DurationSpec.Minutes(m),
        { Start: { } s, End: { } e } => new DurationSpec.Range(s, e),
        { Route: { } r } => new DurationSpec.FromFact(UserFact.RouteKey(r.From, r.To)),
        { Rest: true } => new DurationSpec.Rest(),
        _ => new DurationSpec.Unspecified(),
    };

    /// <summary>
    /// Параллельные пункты (spec §3.6): с временем — по нему; иначе — поверх соседнего основного пункта (предыдущего,
    /// а если параллельный первый — следующего), с его начала на свои минуты или на всю его длину.
    /// </summary>
    private static IEnumerable<Segment> Parallel(
        List<(PeriodItemIntent Item, int Index)> main,
        List<(PeriodItemIntent Item, int Index)> parallel,
        IReadOnlyList<Segment> mainSegments,
        Dictionary<int, Activity?> resolved,
        TimeRange window,
        TimeZoneInfo tz)
    {
        // Основной пункт → его отрезок: по порядку; пункты, исчезнувшие при округлении, пропускаются по названию.
        var byIndex = new Dictionary<int, Segment>();
        var cursor = 0;
        foreach (var (item, index) in main)
        {
            var title = resolved[index]?.Name ?? Capitalize(item.Activity);
            if (cursor < mainSegments.Count && mainSegments[cursor].Title == title)
            {
                byIndex[index] = mainSegments[cursor++];
            }
        }

        foreach (var (item, index) in parallel)
        {
            DateTime from, to;
            if (item is { Start: { } s, End: { } e }
                && PastTime.ResolveInWindow(s, window.StartUtc, window.EndUtc, tz) is { } fs
                && PastTime.ResolveInWindow(e, fs, window.EndUtc, tz) is { } fe)
            {
                (from, to) = (fs, fe);
            }
            else
            {
                var anchorIndex = main.Where(m => m.Index < index).Select(m => (int?)m.Index).LastOrDefault()
                    ?? main.Select(m => (int?)m.Index).FirstOrDefault();
                if (anchorIndex is null || !byIndex.TryGetValue(anchorIndex.Value, out var anchor))
                {
                    continue;
                }

                from = anchor.StartUtc;
                to = item.Minutes is { } m && from.AddMinutes(m) < anchor.EndUtc!.Value ? from.AddMinutes(m) : anchor.EndUtc!.Value;
            }

            if (to <= from)
            {
                continue;
            }

            var activity = resolved[index];
            yield return new Segment(
                from, to, activity?.Name ?? Capitalize(item.Activity), SegmentStatus.Confirmed, SegmentSource.User,
                activity?.Id, activity?.CategoryKey ?? item.Category, isParallel: true);
        }
    }

    /// <summary>«С последнего известного»: начало открытого занятия или конец последней записи (за сутки).</summary>
    private async Task<DateTime?> LastKnownAsync(User user, DateTime end, CancellationToken ct)
    {
        var current = await tracking.CurrentAsync(user, ct);
        if (current is not null && current.Segment.StartUtc < end)
        {
            return current.Segment.StartUtc;
        }

        var timeline = await tracking.LoadAsync(user, end - LastKnownLookback, ct);
        return timeline.Segments
            .Where(s => s.EndUtc is { } e && e <= end && e > end - LastKnownLookback)
            .Select(s => s.EndUtc)
            .Max();
    }

    private static string Explain(IReadOnlyList<ClarificationReason> reasons, List<PeriodItemIntent> items, TimeRange window, TimeZoneInfo tz)
    {
        var span = $"{TrackingReplies.Clock(window.StartUtc, tz)}–{TrackingReplies.Clock(window.EndUtc, tz)}";
        return reasons[0] switch
        {
            ClarificationReason.TooManyUnspecified =>
                $"Не понял, сколько длилось каждое ({string.Join(", ", items.Select(i => i.Activity))}) в {span}. " +
                "Уточни, например: «уборка час, потом магазин».",
            ClarificationReason.Overflow o =>
                $"Не сходится: перечислено {TrackingReplies.Duration(TimeSpan.FromMinutes(o.TotalMinutes))}, а в {span} " +
                $"всего {TrackingReplies.Duration(TimeSpan.FromMinutes(o.WindowMinutes))}. Поправь длительности.",
            _ => $"Время пунктов не укладывается в {span}. Напиши иначе, с часами.",
        };
    }

    // ---- Тексты и контекст ----------------------------------------------------------------------------------

    private async Task<IntentContext> ContextAsync(User user, DateTime now, string? question, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        var dayStart = LocalTime.ToUtc(LocalTime.ToLocal(now, tz).Date, tz);
        var timeline = await tracking.LoadAsync(user, dayStart, ct);
        var current = await tracking.CurrentAsync(user, ct);
        var recent = await tracking.RecentAsync(user, 12, ct);
        return new IntentContext
        {
            NowUtc = now,
            TimeZone = tz,
            OpenActivity = current is null ? null : (current.Segment.Title, current.Segment.StartUtc),
            TodayTimeline = timeline.Segments
                .Concat(timeline.ParallelSegments)
                .Where(s => s.EndUtc is not null && s.StartUtc >= dayStart)
                .OrderBy(s => s.StartUtc)
                .Select(s => $"{TrackingReplies.Clock(s.StartUtc, tz)}–{TrackingReplies.Clock(s.EndUtc!.Value, tz)} {s.Title}{(s.IsParallel ? " (параллельно)" : string.Empty)}")
                .ToList(),
            KnownActivities = recent.Select(a => a.Name).ToList(),
            LastBotQuestion = question,
        };
    }

    private async Task<BotReply> NotUnderstoodAsync(User user, string message, CancellationToken ct)
    {
        var recent = await tracking.RecentAsync(user, TrackingReplies.RecentCount + 1, ct);
        return new BotReply(message, TrackingReplies.Choices(recent, null));
    }

    private static string Lines(IEnumerable<Segment> segments, TimeZoneInfo tz)
    {
        var builder = new StringBuilder();
        foreach (var s in segments.OrderBy(s => s.IsParallel).ThenBy(s => s.StartUtc))
        {
            builder.Append(s.IsParallel ? "↳ параллельно " : string.Empty)
                .Append(TrackingReplies.Clock(s.StartUtc, tz)).Append('–').Append(TrackingReplies.Clock(s.EndUtc!.Value, tz))
                .Append(' ').Append(s.Title)
                .Append(" (").Append(TrackingReplies.Duration(s.EndUtc.Value - s.StartUtc)).AppendLine(")");
        }

        return builder.ToString().TrimEnd();
    }

    private static string Tails(IReadOnlyList<TimeRange> tails, TimeZoneInfo tz) =>
        tails.Count == 0
            ? string.Empty
            : "\n🟡 Не учтено: " + string.Join(", ", tails.Select(t => $"{TrackingReplies.Clock(t.StartUtc, tz)}–{TrackingReplies.Clock(t.EndUtc, tz)}"));

    private static string FactQuestion(string key) => UserFact.ParseRouteKey(key) is { } route
        ? $"Сколько обычно занимает дорога {route.From} → {route.To}? Напиши в минутах, например «25»."
        : $"Сколько это обычно занимает ({key})? Напиши в минутах.";

    private static string FactName(string key) => UserFact.ParseRouteKey(key) is { } route
        ? $"дорога {route.From} → {route.To}"
        : key;

    private static string CategoryName(string key) => DefaultCategories.Find(key)?.Name ?? key;

    private static DateTime NextOccurrence(TimeOnly time, DateTime now, TimeZoneInfo tz)
    {
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(now, tz));
        var candidate = LocalTime.ToUtc(today.ToDateTime(time), tz);
        return candidate > now ? candidate : LocalTime.ToUtc(today.AddDays(1).ToDateTime(time), tz);
    }

    /// <summary>«25», «минут 40», «полчаса», «час», «полтора часа», «2 часа».</summary>
    public static int? ParseMinutes(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Contains("полчаса", StringComparison.Ordinal))
        {
            return 30;
        }

        if (t.Contains("полтора", StringComparison.Ordinal))
        {
            return 90;
        }

        var number = NumberRegex().Match(t);
        var hours = t.Contains("час", StringComparison.Ordinal);
        if (number.Success && double.TryParse(number.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            var minutes = (int)Math.Round(hours ? value * 60 : value);
            return minutes is > 0 and <= 24 * 60 ? minutes : null;
        }

        return hours ? 60 : null;
    }

    private static string Capitalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? trimmed : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    [GeneratedRegex(@"\d+([.,]\d+)?")]
    private static partial Regex NumberRegex();
}
