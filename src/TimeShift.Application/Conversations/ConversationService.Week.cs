using System.Globalization;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Conversations;

/// <summary>Неделя: картинка и цифры по категориям (spec §5.7: /week).</summary>
public sealed partial class ConversationService
{
    private const string WeekButton = "week:";

    /// <summary>Понедельник недели, в которую входит день.</summary>
    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Календарная неделя пн–вс, в которую входит <paramref name="anyDay"/>: прошедшая — целиком, текущая — до «сейчас».
    /// Дни считаются теми же окнами, что и в <c>/day</c>, поэтому суммы недели равны сумме «Итого» её дней. Дни до дня
    /// регистрации не считаются. Сравнение — с той же частью прошлой недели, если она вся после регистрации.
    /// </summary>
    private async Task<BotReply> WeekAsync(User user, DateOnly anyDay, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        var nowMinute = TrackingService.Minute(now);
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(now, tz));
        var firstDay = DateOnly.FromDateTime(LocalTime.ToLocal(user.CreatedAtUtc, tz));
        var thisMonday = Monday(today);
        var monday = Monday(anyDay);
        if (monday > thisMonday)
        {
            monday = thisMonday;
        }

        var title = TrackingReplies.WeekTitle(monday, thisMonday);
        var navigation = WeekNavigation(monday, thisMonday, firstDay);
        var compare = monday.AddDays(-7) >= firstDay;
        var timeline = await tracking.LoadAsync(user, DayStart(compare ? monday.AddDays(-7) : monday, tz), ct);
        var open = await tracking.CurrentAsync(user, ct);
        IReadOnlyList<TimelinePiece> Main(DateTime start, DateTime end) => TimelineBuilder.Build(new TimelineRequest
        {
            Window = new TimeRange(start, end),
            NowUtc = nowMinute,
            Template = user.Template(),
            Segments = timeline.Segments.Where(s => !s.IsOpen).ToList(),
            OpenActivity = open?.ToOpenActivity(),
            Policy = user.Settings.Extension,
        });

        var days = new List<WeekDay>();
        var parallel = new List<TimelinePiece>();
        var previous = compare ? new List<TimelinePiece>() : null;
        for (var day = monday; day < monday.AddDays(7); day = day.AddDays(1))
        {
            var (start, dayEnd) = (DayStart(day, tz), DayStart(day.AddDays(1), tz));
            var end = dayEnd < nowMinute ? dayEnd : nowMinute;
            if (day < firstDay || end <= start)
            {
                days.Add(new WeekDay(day, start, dayEnd, []));
                continue;
            }

            days.Add(new WeekDay(day, start, dayEnd, Main(start, end)));
            parallel.AddRange(TimelineBuilder.BuildParallel(new TimeRange(start, end), timeline.ParallelSegments, nowMinute));
            if (previous is not null)
            {
                // Та же часть дня неделей раньше: целый день — целиком, сегодня — столько же от полуночи.
                var (before, beforeEnd) = (DayStart(day.AddDays(-7), tz), DayStart(day.AddDays(-6), tz));
                var until = end == dayEnd || before + (end - start) > beforeEnd ? beforeEnd : before + (end - start);
                previous.AddRange(Main(before, until));
            }
        }

        if (days.All(d => d.Pieces.Count == 0))
        {
            return new BotReply(monday == thisMonday ? "Неделя только началась." : $"{title}: записей нет.", navigation);
        }

        var stats = WeekStats.Of(days.SelectMany(d => d.Pieces), parallel, previous);
        var image = renderer.RenderWeek(new WeekImage(title, tz, days, stats));
        return new BotReply(TrackingReplies.Week(title, stats), navigation) { Image = image };
    }

    private static DateTime DayStart(DateOnly day, TimeZoneInfo tz) => LocalTime.ToUtc(day.ToDateTime(TimeOnly.MinValue), tz);

    /// <summary>◀ — неделя раньше, пока в ней есть дни после регистрации; ▶ — неделя позже, не дальше текущей.</summary>
    private static IReadOnlyList<IReadOnlyList<ReplyButton>> WeekNavigation(DateOnly monday, DateOnly thisMonday, DateOnly firstDay)
    {
        static string Range(DateOnly from) =>
            from.ToString("d.MM", CultureInfo.InvariantCulture) + "–" + from.AddDays(6).ToString("d.MM", CultureInfo.InvariantCulture);
        var row = new List<ReplyButton>();
        var earlier = monday.AddDays(-7);
        if (earlier.AddDays(6) >= firstDay)
        {
            row.Add(new ReplyButton("◀ " + Range(earlier), $"{WeekButton}{earlier:yyyy-MM-dd}"));
        }

        if (monday < thisMonday)
        {
            var later = monday.AddDays(7);
            row.Add(new ReplyButton(later == thisMonday ? "Эта неделя ▶" : Range(later) + " ▶", $"{WeekButton}{later:yyyy-MM-dd}"));
        }

        return row.Count == 0 ? [] : [row];
    }
}
