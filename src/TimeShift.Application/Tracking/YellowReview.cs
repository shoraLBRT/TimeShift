using System.Globalization;
using TimeShift.Application.Conversations;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tracking;

/// <summary>
/// Разбор 🟡 сразу (spec §5.4): когда пользователь пишет, бот спрашивает про первый ещё не разобранный участок
/// «под вопросом» за последние 12 часов — «Пока тебя не было: 15:30–17:00. Что было?». Ответ кнопкой заполняет
/// участок; сразу следом — следующий участок. Каждый участок предлагается один раз; остатки — вечерний разбор.
/// </summary>
public sealed class YellowReview(TrackingService tracking)
{
    /// <summary>Глубже не смотрим — старое разберёт вечерний обзор (#13).</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(12);

    /// <summary>Короче — не стоит вопроса.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(15);

    private const string WastedTitle = "Залипание";

    /// <summary>Вопрос о следующем неразобранном участке или <c>null</c>.</summary>
    public async Task<BotReply?> NextAsync(User user, DateTime now, CancellationToken ct)
    {
        if (await FirstGapAsync(user, now, ct) is not { } gap)
        {
            return null;
        }

        user.MarkYellowReviewed(gap.EndUtc);
        var tz = user.TimeZoneInfo;
        var recent = await tracking.RecentAsync(user, TrackingReplies.RecentCount, ct);
        var code = $"{Minutes(gap.StartUtc)}:{Minutes(gap.EndUtc)}";
        var buttons = recent
            .Where(a => a.Name != WastedTitle)
            .Select(a => new ReplyButton(a.Name, $"yl:{code}:a:{a.Id:N}"))
            .Append(new ReplyButton("Залипание, так и было", $"yl:{code}:w"))
            .Chunk(2)
            .Select(c => (IReadOnlyList<ReplyButton>)c)
            .ToList();
        buttons.Add([new ReplyButton("✏️ Опишу", $"yl:{code}:d"), new ReplyButton("Пропустить", $"yl:{code}:s")]);

        var day = DateOnly.FromDateTime(LocalTime.ToLocal(gap.StartUtc, tz));
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(now, tz));
        var when = day == today ? string.Empty : day == today.AddDays(-1) ? "вчера " : day.ToString("d MMMM ", CultureInfo.GetCultureInfo("ru-RU"));
        return new BotReply(
            $"🟡 Пока тебя не было: {when}{TrackingReplies.Clock(gap.StartUtc, tz)}–{TrackingReplies.Clock(gap.EndUtc, tz)} " +
            $"({TrackingReplies.Duration(gap.Duration)}) под вопросом. Что было?",
            buttons);
    }

    /// <summary>
    /// Кнопки <c>yl:&lt;начало&gt;:&lt;конец&gt;:&lt;выбор&gt;</c>. Возвращает ответы и, для «Опишу», день для <c>/add</c>.
    /// <c>null</c> — не наша кнопка.
    /// </summary>
    public async Task<(IReadOnlyList<BotReply> Replies, DateOnly? DescribeDay)?> OnButtonAsync(User user, string data, DateTime now, CancellationToken ct)
    {
        var parts = data.Split(':');
        if (parts is not ["yl", var from, var to, var choice, ..]
            || !long.TryParse(from, out var startMinutes) || !long.TryParse(to, out var endMinutes) || endMinutes <= startMinutes)
        {
            return null;
        }

        var tz = user.TimeZoneInfo;
        var range = new TimeRange(FromMinutes(startMinutes), FromMinutes(endMinutes));
        var span = $"{TrackingReplies.Clock(range.StartUtc, tz)}–{TrackingReplies.Clock(range.EndUtc, tz)}";
        Activity? activity = choice switch
        {
            "a" when parts.Length == 5 && Guid.TryParse(parts[4], out var id) => await tracking.GetActivityAsync(user, id, ct),
            "w" => await tracking.ResolveActivityAsync(user, WastedTitle, ct),
            _ => null,
        };

        switch (choice)
        {
            case "s":
                return ([new BotReply($"Ок, {span} оставляю под вопросом.", [], ReplacesSource: true)], null);

            case "d":
                return ([new BotReply($"Напиши, что было в {span}, по строке на запись — например «{TrackingReplies.Clock(range.StartUtc, tz)}-{TrackingReplies.Clock(range.EndUtc, tz)} Магазин».", [], ReplacesSource: true)],
                    DateOnly.FromDateTime(LocalTime.ToLocal(range.StartUtc, tz)));
        }

        if (activity is null)
        {
            return ([new BotReply("Не нашёл это занятие.", [], ReplacesSource: true)], null);
        }

        var segment = new Segment(range.StartUtc, range.EndUtc, activity.Name, SegmentStatus.Confirmed, SegmentSource.User, activity.Id, activity.CategoryKey);
        await tracking.ApplyAsync(user, [segment], now, ct);
        var replies = new List<BotReply>
        {
            new($"✅ {span} — {activity.Name}.", [[new ReplyButton("↩️ Отменить", "tl:undo")]], ReplacesSource: true),
        };
        if (await NextAsync(user, now, ct) is { } next)
        {
            replies.Add(next);
        }

        return (replies, null);
    }

    /// <summary>Первый участок 🟡 (≥ 15 мин) после уже разобранного, в пределах 12 часов и в прошлом.</summary>
    private async Task<TimeRange?> FirstGapAsync(User user, DateTime now, CancellationToken ct)
    {
        var end = TrackingService.Minute(now);
        var from = end - Lookback;
        if (user.YellowReviewedUntilUtc is { } reviewed && reviewed > from)
        {
            from = reviewed;
        }

        if (end - from < MinimumGap)
        {
            return null;
        }

        var timeline = await tracking.LoadAsync(user, from, ct);
        var current = await tracking.CurrentAsync(user, ct);
        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = new TimeRange(from, end),
            NowUtc = end,
            Template = user.Template(),
            Segments = timeline.Segments.Where(s => !s.IsOpen).ToList(),
            OpenActivity = current?.ToOpenActivity(),
            Policy = user.Settings.Extension,
        });

        // Соседние 🟡 (продление + фон) — один участок.
        DateTime? start = null;
        foreach (var piece in pieces.Append(null))
        {
            if (piece is { Status: SegmentStatus.Questionable, IsFuture: false })
            {
                start ??= piece.StartUtc;
                continue;
            }

            if (start is { } s)
            {
                var stop = piece?.StartUtc ?? end;
                if (stop - s >= MinimumGap)
                {
                    return new TimeRange(s, stop);
                }

                start = null;
            }
        }

        return null;
    }

    private static long Minutes(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute;

    private static DateTime FromMinutes(long minutes) => new(minutes * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}
