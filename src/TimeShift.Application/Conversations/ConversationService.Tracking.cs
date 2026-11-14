using System.Globalization;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Conversations;

/// <summary>Учёт после онбординга: кнопки пинга, свободный текст как новое занятие, /now /day /quiet /undo.</summary>
public sealed partial class ConversationService
{
    private const int MaxActivityNameLength = 60;

    /// <summary>Обрабатывает сообщение, если оно про учёт; иначе <c>null</c> — дальше идут меню и настройки.</summary>
    private async Task<IReadOnlyList<BotReply>?> TrackAsync(User user, IncomingMessage message, DateTime now, CancellationToken ct)
    {
        if (message.ButtonData is { } data)
        {
            return await OnTrackingButtonAsync(user, data, now, ct);
        }

        if (message.Text is not { } raw)
        {
            return null;
        }

        var text = raw.Trim();
        if (text.StartsWith('/'))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].Split('@')[0].ToLowerInvariant();
            var argument = parts.Length > 1 ? parts[1] : null;
            IReadOnlyList<BotReply>? handled = command switch
            {
                "/now" => [await NowAsync(user, now, ct)],
                "/day" => [await DayAsync(user, now, ct)],
                "/quiet" => [Quiet(user, argument, now)],
                "/undo" => [await UndoAsync(user, ct)],
                _ => null,
            };
            if (handled is not null)
            {
                user.DialogState = null;
            }

            return handled;
        }

        var state = Parse(user.DialogState);
        if (state is { Step: Step.ActivityName } || state is null)
        {
            user.DialogState = null;
            return await StartByNameAsync(user, text, now, ct);
        }

        return null;
    }

    private async Task<IReadOnlyList<BotReply>?> OnTrackingButtonAsync(User user, string data, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        switch (data)
        {
            case "act:cont":
            {
                var current = await tracking.CurrentAsync(user, ct);
                if (current is null || await tracking.ContinueAsync(user, now, ct) is null)
                {
                    return [await NothingOpenAsync(user, ct)];
                }

                var elapsed = current.Elapsed(TrackingService.Minute(now));
                return [new BotReply($"👍 Продолжаю «{current.Segment.Title}», уже {TrackingReplies.Duration(elapsed)}.", [], ReplacesSource: true)];
            }

            case "act:stop":
            {
                var stopped = await tracking.StopAsync(user, now, ct);
                if (stopped is null)
                {
                    return [await NothingOpenAsync(user, ct)];
                }

                var recent = await tracking.RecentAsync(user, TrackingReplies.RecentCount + 1, ct);
                return
                [
                    new BotReply(TrackingReplies.Stopped(stopped, tz), [], ReplacesSource: true),
                    new BotReply("Чем занят теперь?", TrackingReplies.Choices(recent, stopped.ActivityId)),
                ];
            }

            case "act:other":
                user.DialogState = State(Mode.Edit, Step.ActivityName);
                return [new BotReply("Чем занят? Напиши коротко, например «Готовлю ужин».", [], ReplacesSource: true)];

            case "tl:undo":
                return [await UndoAsync(user, ct) with { ReplacesSource = true }];
        }

        if (data.StartsWith("act:start:", StringComparison.Ordinal))
        {
            if (!Guid.TryParse(data["act:start:".Length..], out var id)
                || await tracking.GetActivityAsync(user, id, ct) is not { } activity)
            {
                return [BotReply.Plain("Не нашёл это занятие.")];
            }

            user.DialogState = null;
            var segment = await tracking.StartAsync(user, activity, now, ct);
            return [TrackingReplies.Started(segment, tz) with { ReplacesSource = true }];
        }

        if (data.StartsWith("quiet:", StringComparison.Ordinal))
        {
            return [Quiet(user, data["quiet:".Length..], now) with { ReplacesSource = true }];
        }

        return null;
    }

    private async Task<IReadOnlyList<BotReply>> StartByNameAsync(User user, string text, DateTime now, CancellationToken ct)
    {
        if (text.Length == 0)
        {
            return [await NowAsync(user, now, ct)];
        }

        if (text.Length > MaxActivityNameLength)
        {
            return
            [
                BotReply.Plain(
                    "Пока я понимаю только короткое название занятия — «Работаю», «Тренировка», «Еду домой». " +
                    "Разбирать описания вроде «час тренировался, потом поехал домой» я научусь чуть позже."),
            ];
        }

        var activity = await tracking.ResolveActivityAsync(user, text, ct);
        var segment = await tracking.StartAsync(user, activity, now, ct);
        return [TrackingReplies.Started(segment, user.TimeZoneInfo)];
    }

    private async Task<BotReply> NowAsync(User user, DateTime now, CancellationToken ct)
    {
        var current = await tracking.CurrentAsync(user, ct);
        var recent = await tracking.RecentAsync(user, TrackingReplies.RecentCount + 1, ct);
        if (current is null)
        {
            return new BotReply("Сейчас ничего не записано — идёт фон. Чем занят?", TrackingReplies.Choices(recent, null));
        }

        var ping = TrackingReplies.Ping(current, recent, now, user.TimeZoneInfo);
        return ping with
        {
            Text = $"Сейчас: «{current.Segment.Title}» с {TrackingReplies.Clock(current.Segment.StartUtc, user.TimeZoneInfo)}, " +
                   $"идёт {TrackingReplies.Duration(current.Elapsed(now))}.",
        };
    }

    private async Task<BotReply> NothingOpenAsync(User user, CancellationToken ct)
    {
        var recent = await tracking.RecentAsync(user, TrackingReplies.RecentCount + 1, ct);
        return new BotReply("Сейчас нет начатого занятия. Чем занят?", TrackingReplies.Choices(recent, null), ReplacesSource: true);
    }

    private async Task<BotReply> DayAsync(User user, DateTime now, CancellationToken ct)
    {
        var tz = user.TimeZoneInfo;
        var localNow = LocalTime.ToLocal(now, tz);
        var dayStart = LocalTime.ToUtc(localNow.Date, tz);
        var end = TrackingService.Minute(now);
        if (end <= dayStart)
        {
            return BotReply.Plain("День только начался.");
        }

        var timeline = await tracking.LoadAsync(user, dayStart, ct);
        var open = await tracking.CurrentAsync(user, ct);
        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = new TimeRange(dayStart, end),
            NowUtc = end,
            Template = user.Template(),
            Segments = timeline.Segments.Where(s => !s.IsOpen).ToList(),
            OpenActivity = open?.ToOpenActivity(),
            Policy = user.Settings.Extension,
        });
        return BotReply.Plain(TrackingReplies.Day(pieces, tz, now));
    }

    private static BotReply Quiet(User user, string? argument, DateTime now)
    {
        var tz = user.TimeZoneInfo;
        if (argument is "off" or "0")
        {
            user.SetQuietUntil(null);
            return BotReply.Plain("🔔 Тихие часы сняты, снова буду спрашивать.");
        }

        if (argument is not null && TryHours(argument, out var hours))
        {
            var until = TrackingService.Minute(now + hours);
            user.SetQuietUntil(until);
            return BotReply.Plain($"🔕 Не беспокою до {TrackingReplies.Clock(until, tz)}. Снять: /quiet off");
        }

        var status = user.QuietUntilUtc is { } q && q > now
            ? $"Сейчас тихо до {TrackingReplies.Clock(q, tz)}. "
            : string.Empty;
        return new BotReply(
            status + "На сколько не беспокоить? Можно и текстом: /quiet 3",
            [
                [new ReplyButton("1 ч", "quiet:60m"), new ReplyButton("2 ч", "quiet:120m"), new ReplyButton("4 ч", "quiet:240m")],
                [new ReplyButton("🔔 Снять тихие часы", "quiet:off")],
            ]);
    }

    /// <summary>«3» или «3ч» — часы, «90m» — минуты (из кнопок); от 5 минут до суток.</summary>
    private static bool TryHours(string text, out TimeSpan duration)
    {
        duration = default;
        var t = text.Trim().ToLowerInvariant().Replace(',', '.');
        double value;
        if (t.EndsWith('m') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            duration = TimeSpan.FromMinutes(value);
        }
        else if (double.TryParse(t.TrimEnd('ч', 'h', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            duration = TimeSpan.FromHours(value);
        }
        else
        {
            return false;
        }

        return duration >= TimeSpan.FromMinutes(5) && duration <= TimeSpan.FromDays(1);
    }

    private async Task<BotReply> UndoAsync(User user, CancellationToken ct) =>
        await tracking.UndoAsync(user, ct) switch
        {
            UndoResult.Undone => BotReply.Plain("↩️ Отменил последнюю запись. /day — посмотреть ленту."),
            UndoResult.Stale => BotReply.Plain("Лента с тех пор поменялась — эту запись уже не отменить целиком."),
            _ => BotReply.Plain("Отменять нечего."),
        };
}
