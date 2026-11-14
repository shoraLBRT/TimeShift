using System.Globalization;
using System.Text;
using TimeShift.Application.Llm;
using TimeShift.Application.Parsing;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Conversations;

/// <summary>
/// <c>/add</c> — записи задним числом без LLM (spec §5.6): «вчера 19-21 Зал», по строке на запись. Без дня — последнее
/// прошедшее время (в 6 утра «19-21» — вчера); с кнопки «➕ Добавить» в <c>/day</c> — день той ленты.
/// </summary>
public sealed partial class ConversationService
{
    /// <summary>Диапазон длиннее — подозрительно («с 11 до 2» в 6 утра — 15 часов?): переспросить.</summary>
    private static readonly TimeSpan AmbiguousRange = TimeSpan.FromHours(12);

    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    private static BotReply AskAddPast(User user, DateOnly? day, DateTime now)
    {
        user.DialogState = day is { } d ? $"E:{Step.AddPast}:{d:yyyy-MM-dd}" : $"E:{Step.AddPast}";
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(now, user.TimeZoneInfo));
        var when = day is { } dd && dd != today ? $"{dd.ToString("d MMMM", RuCulture)}" : null;
        return BotReply.Plain(when is null
            ? "Что и когда было? По строке на запись, например:\nвчера 19-21 Зал\n13.11 20:00-23:30 Друзья\n7-8 Пробежка"
            : $"Что было {when}? По строке на запись, например:\n19-21 Зал\n22:30-23:30 Сериал");
    }

    private async Task<IReadOnlyList<BotReply>> AddPastAsync(User user, string text, DateOnly? defaultDay, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        var today = DateOnly.FromDateTime(LocalTime.ToLocal(now, tz));
        var segments = new List<Segment>();
        var problems = new List<string>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ScheduleText.TryFindTimeRange(line, out var from, out var to, out var before, out var after))
            {
                problems.Add($"«{line}» — не нашёл время, напиши, например, «19-21 Зал».");
                continue;
            }

            // [день] время название, или название время (тогда день — по умолчанию).
            DateOnly? day = defaultDay;
            var title = after;
            if (before.Length > 0)
            {
                if (DayArgument.TryParse(before, today, out var parsed, out _))
                {
                    day = parsed;
                }
                else if (title.Length == 0)
                {
                    title = before;
                }
                else
                {
                    problems.Add($"«{line}» — не понял день «{before}».");
                    continue;
                }
            }

            if (title.Length == 0)
            {
                problems.Add($"«{line}» — чем был занят? Добавь название, например «{line} Зал».");
                continue;
            }

            var window = PastTime.ResolveWindow(day is { } d ? new DayRef.Date(d) : null, from, to, null, now, tz);
            if (window.Window is not { StartUtc: { } start } range)
            {
                problems.Add($"«{line}» — это ещё не наступило: записываю только прошлое.");
                continue;
            }

            var end = TrackingService.Minute(range.EndUtc);
            start = TrackingService.Minute(start);
            if (day is null && end - start > AmbiguousRange)
            {
                problems.Add($"«{line}» — это {TrackingReplies.Clock(start, tz)}–{TrackingReplies.Clock(end, tz)}, больше 12 часов. " +
                             "Если имелся в виду вечер — напиши, например, 23-2.");
                continue;
            }

            if (end <= start)
            {
                problems.Add($"«{line}» — получился пустой отрезок.");
                continue;
            }

            if (segments.Any(s => s.StartUtc < end && start < s.EndUtc))
            {
                problems.Add($"«{line}» — пересекается с другой строкой.");
                continue;
            }

            var activity = await tracking.ResolveActivityAsync(user, title, ct);
            activity.CategoryKey ??= ScheduleText.GuessCategory(activity.Name);
            segments.Add(new Segment(start, end, activity.Name, SegmentStatus.Confirmed, SegmentSource.User, activity.Id, activity.CategoryKey));
        }

        var replies = new List<BotReply>();
        if (segments.Count > 0)
        {
            await tracking.ApplyAsync(user, segments, now, ct);
            var builder = new StringBuilder("✅ Записал:");
            foreach (var s in segments.OrderBy(s => s.StartUtc))
            {
                var date = DateOnly.FromDateTime(LocalTime.ToLocal(s.StartUtc, tz));
                builder.Append('\n').Append(date == today ? string.Empty : date.ToString("d MMMM, ", RuCulture))
                    .Append(TrackingReplies.Clock(s.StartUtc, tz)).Append('–').Append(TrackingReplies.Clock(s.EndUtc!.Value, tz))
                    .Append(' ').Append(s.Title);
            }

            var days = segments.Select(s => DateOnly.FromDateTime(LocalTime.ToLocal(s.StartUtc, tz))).Distinct().Order().ToList();
            replies.Add(new BotReply(
                builder.ToString(),
                [[new ReplyButton("↩️ Отменить", "tl:undo"), new ReplyButton("📅 Лента дня", $"day:{days[^1]:yyyy-MM-dd}")]]));
        }

        if (problems.Count > 0)
        {
            replies.Add(BotReply.Plain(string.Join('\n', problems) + "\n\nПоправь эти строки и пришли ещё раз — /add."));
        }

        return replies;
    }
}
