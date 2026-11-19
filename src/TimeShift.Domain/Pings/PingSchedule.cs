using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Pings;

/// <summary>Всё, от чего зависит время следующего пинга.</summary>
public sealed class PingContext
{
    /// <summary>Сейчас: пинг не бывает в прошлом, а запреты (сон, блоки) проверяются от этого момента.</summary>
    public required DateTime NowUtc { get; init; }

    public required TimeSpan Interval { get; init; }

    public DateTime? LastPingAtUtc { get; init; }

    /// <summary>Последнее сообщение пользователя: после него пинг — не раньше чем через интервал.</summary>
    public DateTime? LastSeenAtUtc { get; init; }

    public int UnansweredPings { get; init; }

    public int MaxUnansweredPings { get; init; } = 2;

    /// <summary>Что после <see cref="MaxUnansweredPings"/> неотвеченных: затухание или пауза.</summary>
    public UnansweredPingsPolicy AfterLimit { get; init; } = UnansweredPingsPolicy.BackOff;

    /// <summary>Пинги выключены совсем (настройка пользователя).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Ежедневные часы без пингов, в часовом поясе шаблона.</summary>
    public QuietHours? QuietHours { get; init; }

    /// <summary>Открытое занятие; с известной типичной длительностью первый пинг — по её достижении.</summary>
    public OpenActivity? OpenActivity { get; init; }

    public required WeekTemplate Template { get; init; }

    /// <summary>Пинговать во время постоянных и своих блоков (сон — никогда).</summary>
    public bool PingDuringBlocks { get; init; }

    public DateTime? QuietUntilUtc { get; init; }
}

/// <summary>
/// Когда пинговать (spec §5.2). Чистая функция: возвращает момент следующего пинга или <c>null</c> — пингов нет
/// (выключены или пауза до сообщения пользователя).
/// <list type="number">
/// <item>Пинги выключены → <c>null</c>.</item>
/// <item>Через интервал после последнего пинга или сообщения пользователя (что позже).</item>
/// <item>Неотвеченных пингов ≥ предела: при паузе → <c>null</c>, при затухании интервал удваивается за каждый
/// пинг сверх предела без одного (30 → 60 → 120 → 240 мин), но не дольше 4 ч (или самого интервала, если он больше).</item>
/// <item>Во время открытого занятия — не раньше, чем оно достигнет типичной длительности (с учётом уже прожитого
/// до перерыва).</item>
/// <item>Не во сне, не в тихие часы (/quiet и ежедневные) и (по настройке) не в блоках — тогда сразу после их конца.</item>
/// </list>
/// </summary>
public static class PingSchedule
{
    private const int MaxShifts = 32;

    /// <summary>Потолок затухания (spec §5.2).</summary>
    public static readonly TimeSpan MaxBackOffInterval = TimeSpan.FromHours(4);

    public static DateTime? NextDue(PingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Template);
        if (context.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(context), "Интервал пингов должен быть > 0.");
        }

        if (!context.Enabled)
        {
            return null;
        }

        var interval = context.Interval;
        if (context.UnansweredPings >= context.MaxUnansweredPings)
        {
            if (context.AfterLimit == UnansweredPingsPolicy.Stop)
            {
                return null;
            }

            interval = BackOff(context.Interval, context.UnansweredPings - context.MaxUnansweredPings + 1);
        }

        var anchor = Max(context.LastPingAtUtc, context.LastSeenAtUtc);
        if (anchor is null)
        {
            return null;
        }

        var due = anchor.Value + interval;
        if (context.OpenActivity is { TypicalDuration: { } typical } open)
        {
            var reached = open.StartUtc + typical - open.ElapsedBefore;
            if (reached > due)
            {
                due = reached;
            }
        }

        // Просроченный пинг (бот был выключен, пользователь спал) — не раньше «сейчас».
        if (due < context.NowUtc)
        {
            due = context.NowUtc;
        }

        // Сдвигаем за тихие часы и блоки, пока не попадём в разрешённое время (блоки могут идти подряд).
        for (var i = 0; i < MaxShifts; i++)
        {
            if (context.QuietUntilUtc is { } quiet && quiet > due)
            {
                due = quiet;
                continue;
            }

            if (context.QuietHours?.EndIfInside(due, context.Template.TimeZone) is { } quietEnd)
            {
                due = quietEnd;
                continue;
            }

            var blocking = context.Template
                .Occurrences(new TimeRange(due, due.AddTicks(1)))
                .FirstOrDefault(o => o.Range.Contains(due) && Suppresses(o.Block, context.PingDuringBlocks));
            if (blocking is null)
            {
                return due;
            }

            due = blocking.Range.EndUtc;
        }

        return due;
    }

    /// <summary>Интервал, удвоенный <paramref name="doublings"/> раз, но не дольше потолка затухания.</summary>
    public static TimeSpan BackOff(TimeSpan interval, int doublings)
    {
        var cap = interval > MaxBackOffInterval ? interval : MaxBackOffInterval;
        var result = interval;
        for (var i = 0; i < doublings && result < cap; i++)
        {
            result *= 2;
        }

        return result < cap ? result : cap;
    }

    private static bool Suppresses(ScheduleBlock block, bool pingDuringBlocks) =>
        block.Kind == ScheduleBlockKind.Sleep || !pingDuringBlocks;

    private static DateTime? Max(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
