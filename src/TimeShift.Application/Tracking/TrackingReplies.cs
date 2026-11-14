using System.Text;
using TimeShift.Application.Conversations;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;

namespace TimeShift.Application.Tracking;

/// <summary>Тексты и кнопки учёта: пинг, «чем занят теперь», лента дня.</summary>
public static class TrackingReplies
{
    public const int RecentCount = 4;

    /// <summary>Пинг (spec §5.2): во время занятия — «Всё ещё …?», иначе — «Чем занят?».</summary>
    public static BotReply Ping(CurrentActivity? current, IReadOnlyList<Activity> recent, DateTime nowUtc, TimeZoneInfo timeZone)
    {
        if (current is null)
        {
            return new BotReply("Чем занят?", Choices(recent, exceptActivityId: null));
        }

        var segment = current.Segment;
        var text = $"Всё ещё «{segment.Title}»? Идёт {Duration(current.Elapsed(nowUtc))}.";
        var rows = new List<IReadOnlyList<ReplyButton>>
        {
            new[] { new ReplyButton($"▶️ Да, {segment.Title}", "act:cont"), new ReplyButton("⏹ Закончил", "act:stop") },
        };
        rows.AddRange(Choices(recent, segment.ActivityId));
        return new BotReply(text, rows);
    }

    /// <summary>Недавние занятия (по два в ряд) и «Другое…».</summary>
    public static List<IReadOnlyList<ReplyButton>> Choices(IReadOnlyList<Activity> recent, Guid? exceptActivityId)
    {
        var buttons = recent
            .Where(a => a.Id != exceptActivityId)
            .Take(RecentCount)
            .Select(a => new ReplyButton(a.Name, $"act:start:{a.Id}"))
            .Append(new ReplyButton("✏️ Другое…", "act:other"));
        return buttons.Chunk(2).Select(c => (IReadOnlyList<ReplyButton>)c).ToList();
    }

    public static BotReply Started(Segment segment, TimeZoneInfo timeZone) => new(
        $"▶️ Записал: «{segment.Title}» с {Clock(segment.StartUtc, timeZone)}.",
        [[new ReplyButton("⏹ Закончил", "act:stop"), new ReplyButton("↩️ Отменить", "tl:undo")]]);

    public static string Stopped(Segment segment, TimeZoneInfo timeZone) => segment.EndUtc is { } end
        ? $"⏹ «{segment.Title}»: {Clock(segment.StartUtc, timeZone)}–{Clock(end, timeZone)} ({Duration(end - segment.StartUtc)})."
        : $"⏹ «{segment.Title}» убрал — оно только началось.";

    /// <summary>Лента дня текстом (spec §5.7: /day; картинка — шаг 8).</summary>
    public static string Day(
        IReadOnlyList<TimelinePiece> pieces, IReadOnlyList<TimelinePiece> parallel, TimeZoneInfo timeZone, DateOnly day, DateOnly today)
    {
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var name = day == today ? "Сегодня" : day == today.AddDays(-1) ? "Вчера" : day.ToString("dddd", ru);
        var builder = new StringBuilder();
        builder.Append(char.ToUpper(name[0], ru)).Append(name[1..]).Append(", ").Append(day.ToString("d MMMM", ru)).AppendLine(":").AppendLine();
        foreach (var piece in pieces)
        {
            builder
                .Append(Clock(piece.StartUtc, timeZone)).Append('–').Append(Clock(piece.EndUtc, timeZone)).Append(' ')
                .Append(Icon(piece.Status)).Append(' ').Append(piece.Title)
                .Append(" (").Append(Duration(piece.Duration)).AppendLine(")");
        }

        if (parallel.Count > 0)
        {
            builder.AppendLine().AppendLine("Параллельно:");
            foreach (var piece in parallel)
            {
                builder
                    .Append("↳ ").Append(Clock(piece.StartUtc, timeZone)).Append('–').Append(Clock(piece.EndUtc, timeZone)).Append(' ')
                    .Append(Icon(piece.Status)).Append(' ').Append(piece.Title)
                    .Append(" (").Append(Duration(piece.Duration)).AppendLine(")");
            }
        }

        builder.AppendLine().Append("✅ подтверждено · ⚪ предполагаю · 🟡 под вопросом");
        return builder.ToString();
    }

    public static string Icon(SegmentStatus status) => status switch
    {
        SegmentStatus.Confirmed => "✅",
        SegmentStatus.Assumed => "⚪",
        _ => "🟡",
    };

    public static string Clock(DateTime utc, TimeZoneInfo timeZone) => LocalTime.ToLocal(utc, timeZone).ToString("HH:mm");

    /// <summary>«40 мин», «1 ч», «2 ч 15 мин».</summary>
    public static string Duration(TimeSpan duration)
    {
        var minutes = (int)Math.Round(Math.Max(0, duration.TotalMinutes));
        var (h, m) = (minutes / 60, minutes % 60);
        return (h, m) switch
        {
            (0, _) => $"{m} мин",
            (_, 0) => $"{h} ч",
            _ => $"{h} ч {m} мин",
        };
    }
}
