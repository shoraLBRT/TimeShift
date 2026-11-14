using TimeShift.Application.Conversations;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tracking;

/// <summary>
/// Вечерний разбор и утренний вопрос (spec §5.4). Вечером — лента дня, оставшиеся 🟡 по одному и «Остальное
/// верно?» для ⚪; утром, первым сообщением после сна по шаблону, — «Спал как обычно?». Оба — раз в день.
/// </summary>
public sealed class DailyReview(TrackingService tracking, YellowReview yellow)
{
    /// <summary>После начала сна по шаблону вечерний разбор ещё уместен столько времени (потом человек уже спит).</summary>
    private static readonly TimeSpan EveningGrace = TimeSpan.FromMinutes(30);

    private const string SleepTitle = "Сон";

    /// <summary>Пора ли вечерний разбор: от «за N минут до сна» до двух часов после его начала, раз в день.</summary>
    public bool IsEveningDue(User user, DateTime now)
    {
        var offset = TimeSpan.FromMinutes(user.Settings.EveningReviewOffsetMin);
        if (user.LastEveningReview == ReviewDay(user, now))
        {
            return false;
        }

        // Конец окна включительно: сон, который начнётся ровно через offset, тоже должен попасть.
        return SleepOccurrences(user, now - EveningGrace, now + offset + TimeSpan.FromMinutes(1))
            .Any(o => o.Range.StartUtc - offset <= now && now < o.Range.StartUtc + EveningGrace);
    }

    /// <summary>Сообщение вечернего разбора за сегодня (отмечает разбор сделанным).</summary>
    public async Task<BotReply> EveningAsync(User user, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        var today = ReviewDay(user, now);
        user.MarkEveningReview(today);
        var dayStart = DayStart(user, today);
        var pieces = await PiecesAsync(user, dayStart, now, ct);
        var gaps = await yellow.GapsAsync(user, now, dayStart, ct);
        var assumed = Assumable(pieces, await tracking.CurrentAsync(user, ct));

        var text = "🌙 Итоги дня.\n\n" + TrackingReplies.Day(pieces, [], tz, today, today);
        text += gaps.Count == 0
            ? "\n\n🟡 Под вопросом ничего не осталось."
            : "\n\n🟡 Осталось разобрать: " + string.Join(", ", gaps.Select(g => $"{TrackingReplies.Clock(g.StartUtc, tz)}–{TrackingReplies.Clock(g.EndUtc, tz)}"));
        if (assumed.Count > 0)
        {
            text += "\n⚪ Остальное верно?";
        }

        var row = new List<ReplyButton>();
        if (gaps.Count > 0)
        {
            row.Add(new ReplyButton($"🟡 Разобрать ({gaps.Count})", "ev:yellow"));
        }

        if (assumed.Count > 0)
        {
            row.Add(new ReplyButton("⚪ Остальное верно", "ev:ok"));
        }

        return new BotReply(text, row.Count == 0 ? [] : [row, [new ReplyButton("✏️ Поправить", "ev:fix")]]);
    }

    /// <summary>Утренний вопрос, если это первое сообщение после сна по шаблону сегодня; иначе <c>null</c>.</summary>
    public async Task<BotReply?> MorningAsync(User user, DateTime now, CancellationToken ct)
    {
        var today = Today(user, now);
        // Ночь до регистрации — спрашивать не о чем.
        if (user.LastMorningCheck == today || LastNight(user, now) is not { } night || night.StartUtc < user.CreatedAtUtc)
        {
            return null;
        }

        user.MarkMorningCheck(today);

        // Сон уже записан явно — спрашивать нечего.
        var timeline = await tracking.LoadAsync(user, night.StartUtc, ct);
        if (timeline.Segments.Any(s => s.StartUtc < night.EndUtc && (s.EndUtc ?? DateTime.MaxValue) > night.StartUtc && s.Source == SegmentSource.User))
        {
            return null;
        }

        var tz = user.TimeZoneInfo;
        return new BotReply(
            $"☀️ Доброе утро! Спал как обычно, {TrackingReplies.Clock(night.StartUtc, tz)}–{TrackingReplies.Clock(night.EndUtc, tz)}?",
            [[new ReplyButton("Да", "mo:yes"), new ReplyButton("Нет, по-другому", "mo:no")]]);
    }

    /// <summary>Кнопки <c>ev:*</c> и <c>mo:*</c>. <c>null</c> — не наша кнопка. <c>AskSleep</c> — ждать «во сколько лёг и встал».</summary>
    public async Task<(IReadOnlyList<BotReply> Replies, bool AskSleep)?> OnButtonAsync(User user, string data, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        switch (data)
        {
            case "ev:yellow":
            {
                var next = await yellow.NextAsync(user, now, ct, after: DayStart(user, ReviewDay(user, now)));
                return ([next ?? BotReply.Plain("🟡 Под вопросом ничего не осталось.")], false);
            }

            case "ev:ok":
            {
                var pieces = await PiecesAsync(user, DayStart(user, ReviewDay(user, now)), now, ct);
                var segments = Assumable(pieces, await tracking.CurrentAsync(user, ct))
                    .Select(p => new Segment(p.StartUtc, p.EndUtc, p.Title, SegmentStatus.Confirmed, SegmentSource.User, p.ActivityId, p.CategoryKey))
                    .ToList();
                await tracking.ApplyAsync(user, segments, now, ct);
                return ([new BotReply($"✅ Подтвердил {segments.Count} ⚪. Спокойной ночи!", [[new ReplyButton("↩️ Отменить", "tl:undo")]], ReplacesSource: false)], false);
            }

            case "ev:fix":
                return ([BotReply.Plain("Напиши, что было не так: «с 19 до 21 был в зале» или /add — по строке на запись.")], false);

            case "mo:yes":
            {
                if (LastNight(user, now) is not { } night)
                {
                    return ([new BotReply("Не нашёл сна в шаблоне — запиши через /add.", [], ReplacesSource: true)], false);
                }

                await tracking.ApplyAsync(user, [SleepSegment(night.StartUtc, night.EndUtc)], now, ct);
                return ([new BotReply($"✅ Сон {TrackingReplies.Clock(night.StartUtc, tz)}–{TrackingReplies.Clock(night.EndUtc, tz)}.", [[new ReplyButton("↩️ Отменить", "tl:undo")]], ReplacesSource: true)], false);
            }

            case "mo:no":
                return ([new BotReply("Во сколько лёг и встал? Например «2-10» или «23:30-7:15».", [], ReplacesSource: true)], true);

            default:
                return null;
        }
    }

    /// <summary>«Во сколько лёг и встал» → сон прошлой ночью (последнее прошедшее вхождение).</summary>
    public async Task<BotReply> RecordSleepAsync(User user, TimeOnly from, TimeOnly to, DateTime now, CancellationToken ct)
    {
        var window = Llm.PastTime.ResolveWindow(null, from, to, null, now, user.TimeZoneInfo);
        if (window.Window is not { StartUtc: { } start } range)
        {
            return BotReply.Plain("Это время ещё не наступило. Напиши, например, «2-10».");
        }

        var segment = SleepSegment(TrackingService.Minute(start), TrackingService.Minute(range.EndUtc));
        await tracking.ApplyAsync(user, [segment], now, ct);
        var tz = user.TimeZoneInfo;
        return new BotReply(
            $"✅ Сон {TrackingReplies.Clock(segment.StartUtc, tz)}–{TrackingReplies.Clock(segment.EndUtc!.Value, tz)} " +
            $"({TrackingReplies.Duration(segment.EndUtc.Value - segment.StartUtc)}).",
            [[new ReplyButton("↩️ Отменить", "tl:undo")]]);
    }

    private static Segment SleepSegment(DateTime start, DateTime end) =>
        new(start, end, SleepTitle, SegmentStatus.Confirmed, SegmentSource.User, categoryKey: DefaultCategories.Sleep);

    /// <summary>⚪, которые можно подтвердить: закончились, и это не продление ещё идущего занятия.</summary>
    private static List<TimelinePiece> Assumable(IReadOnlyList<TimelinePiece> pieces, CurrentActivity? current) =>
        pieces.Where(p => p.Status == SegmentStatus.Assumed && !p.IsFuture
                          && !(p.Source == SegmentSource.Extension && current is not null))
            .ToList();

    private async Task<IReadOnlyList<TimelinePiece>> PiecesAsync(User user, DateTime from, DateTime now, CancellationToken ct)
    {
        var end = TrackingService.Minute(now);
        var timeline = await tracking.LoadAsync(user, from, ct);
        var current = await tracking.CurrentAsync(user, ct);
        return end <= from
            ? []
            : TimelineBuilder.Build(new TimelineRequest
            {
                Window = new TimeRange(from, end),
                NowUtc = end,
                Template = user.Template(),
                Segments = timeline.Segments.Where(s => !s.IsOpen).ToList(),
                OpenActivity = current?.ToOpenActivity(),
                Policy = user.Settings.Extension,
            });
    }

    /// <summary>Сон по шаблону, закончившийся за последние 12 часов.</summary>
    private static TimeRange? LastNight(User user, DateTime now)
    {
        var night = SleepOccurrences(user, now - TimeSpan.FromHours(30), now)
            .Where(o => o.Range.EndUtc <= now && o.Range.EndUtc > now - TimeSpan.FromHours(12))
            .OrderBy(o => o.Range.EndUtc)
            .LastOrDefault();
        return night?.Range;
    }

    private static IEnumerable<BlockOccurrence> SleepOccurrences(User user, DateTime from, DateTime to) =>
        user.Template().Occurrences(new TimeRange(from, to)).Where(o => o.Block.Kind == ScheduleBlockKind.Sleep);

    /// <summary>
    /// День, который подводит вечерний разбор: до 6 утра — ещё вчерашний (сон в 01:00, разбор в 00:30 — про вчера).
    /// </summary>
    public static DateOnly ReviewDay(User user, DateTime now) => Today(user, now - TimeSpan.FromHours(6));

    private static DateOnly Today(User user, DateTime now) => DateOnly.FromDateTime(LocalTime.ToLocal(now, user.TimeZoneInfo));

    /// <summary>Начало дня, но не раньше регистрации — время до неё не разбираем.</summary>
    private static DateTime DayStart(User user, DateOnly day)
    {
        var start = LocalTime.ToUtc(day.ToDateTime(TimeOnly.MinValue), user.TimeZoneInfo);
        return start < user.CreatedAtUtc ? TrackingService.Minute(user.CreatedAtUtc) : start;
    }
}
