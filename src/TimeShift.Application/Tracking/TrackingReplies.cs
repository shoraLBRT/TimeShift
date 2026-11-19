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
            return new BotReply("Чем занят?", Choices(recent, exceptActivityId: null))
            {
                Notice = new ClientNotice(NoticeKind.Ping, "Чем занят?", "Открой, чтобы выбрать занятие."),
            };
        }

        var segment = current.Segment;
        var text = $"Всё ещё «{segment.Title}»? Идёт {Duration(current.Elapsed(nowUtc))}.";
        var rows = new List<IReadOnlyList<ReplyButton>>
        {
            new[] { new ReplyButton($"▶️ Да, {segment.Title}", "act:cont"), new ReplyButton("⏹ Закончил", "act:stop") },
        };
        rows.AddRange(Choices(recent, segment.ActivityId));
        return new BotReply(text, rows)
        {
            Notice = new ClientNotice(
                NoticeKind.Ping, $"Всё ещё «{segment.Title}»?", $"Идёт {Duration(current.Elapsed(nowUtc))}", CanAct: true),
        };
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
                .Append(Icon(piece.Status)).Append(' ').Append(Title(piece))
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

    /// <summary>Приписка к ответу о категории занятия, когда категорию получила и идущая запись (#62).</summary>
    public static string CurrentEntryCategorized(Segment entry, Domain.Categories.CategorySet categories) =>
        $"Текущая запись теперь — {categories.Name(entry.CategoryKey!)} (отменить — /undo).";

    /// <summary>Название куска в ленте: фон в прошлом — «Не записано», в будущем — «Свободно» (#47).</summary>
    public static string Title(TimelinePiece piece) =>
        piece.IsUnknown && piece.IsFuture ? FreeTitle : piece.Title;

    /// <summary>Строка итогов для неизвестного времени (#47): не категория и не «Потерянное».</summary>
    public const string UnknownTotalsName = TimelineBuilder.BackgroundTitle;

    private const string FreeTitle = "Свободно";

    /// <summary>
    /// Итоги по категориям (spec §5.7): основная дорожка — сумма = длина окна; неизвестное время (фон) — отдельной
    /// строкой «Не записано», не в категориях (#47); параллельные — отдельно.
    /// </summary>
    public static string Totals(
        IReadOnlyList<TimelinePiece> pieces, IReadOnlyList<TimelinePiece> parallel, Domain.Categories.CategorySet? categories = null)
    {
        var set = categories ?? Domain.Categories.CategorySet.System;
        string Name(string? key) => key is null ? "Без категории" : set.Name(key);
        var past = pieces.Where(p => !p.IsFuture).ToList();
        var main = past.Where(p => !p.IsUnknown)
            .GroupBy(p => set.TotalsKey(p.CategoryKey))
            .Select(g => (Name: Name(g.Key), Total: TimeSpan.FromTicks(g.Sum(p => p.Duration.Ticks))))
            .OrderByDescending(x => x.Total)
            .Select(x => $"{x.Name} {Duration(x.Total)}")
            .ToList();
        var unknown = TimeSpan.FromTicks(past.Where(p => p.IsUnknown).Sum(p => p.Duration.Ticks));
        var text = main.Count == 0 && unknown > TimeSpan.Zero
            ? $"Итого: {UnknownTotalsName} {Duration(unknown)}"
            : "Итого: " + string.Join(" · ", main) + (unknown > TimeSpan.Zero ? $"\n{UnknownTotalsName}: {Duration(unknown)}" : string.Empty);
        if (parallel.Count > 0)
        {
            text += "\nПараллельно: " + string.Join(" · ", parallel
                .GroupBy(p => p.Title)
                .Select(g => $"{g.Key} {Duration(TimeSpan.FromTicks(g.Sum(p => p.Duration.Ticks)))}"));
        }

        return text;
    }

    /// <summary>«Эта неделя, 16–22 ноября», «Прошлая неделя, 9–15 ноября», «Неделя 30 ноября – 6 декабря».</summary>
    public static string WeekTitle(DateOnly monday, DateOnly thisMonday)
    {
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var sunday = monday.AddDays(6);
        var range = monday.Month == sunday.Month
            ? $"{monday.Day}–{sunday.ToString("d MMMM", ru)}"
            : $"{monday.ToString("d MMMM", ru)} – {sunday.ToString("d MMMM", ru)}";
        return monday == thisMonday ? "Эта неделя, " + range
            : monday == thisMonday.AddDays(-7) ? "Прошлая неделя, " + range
            : "Неделя " + range;
    }

    /// <summary>
    /// Цифры недели текстом (spec §5.7: /week): категории основной дорожки (в сумме — «Всего»), параллельные отдельной
    /// строкой, доли ✅/⚪/🟡; в скобках — разница с прошлой неделей.
    /// </summary>
    public static string Week(string title, WeekStats stats)
    {
        var builder = new StringBuilder();
        builder.Append(title).AppendLine(":").AppendLine();
        foreach (var category in stats.Categories)
        {
            builder.Append(category.Name).Append(' ').Append(Duration(category.Total));
            if (category.Change is { } change)
            {
                builder.Append(" (").Append(Change(change)).Append(')');
            }

            builder.AppendLine();
        }

        builder.Append("Всего: ").AppendLine(Duration(stats.Total));
        if (stats.Parallel.Count > 0)
        {
            builder.AppendLine().Append("Параллельно: ")
                .AppendLine(string.Join(" · ", stats.Parallel.Select(p => $"{p.Title} {Duration(p.Total)}")));
        }

        var (confirmed, assumed, questionable) = stats.Shares();
        builder.AppendLine().Append("✅ ").Append(confirmed).Append("% · ⚪ ").Append(assumed).Append("% · 🟡 ").Append(questionable).Append('%');
        if (stats.IsCompared)
        {
            builder.AppendLine().Append("В скобках — разница с теми же днями прошлой недели.");
        }

        return builder.ToString();
    }

    /// <summary>«+2 ч», «−40 мин», «=» — разница с прошлой неделей.</summary>
    public static string Change(TimeSpan change) => Math.Abs(change.TotalMinutes) < 0.5
        ? "="
        : (change > TimeSpan.Zero ? "+" : "−") + Duration(change.Duration());

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
