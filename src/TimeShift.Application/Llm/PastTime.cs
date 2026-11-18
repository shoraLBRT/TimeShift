using TimeShift.Domain.Schedule;

namespace TimeShift.Application.Llm;

/// <summary>
/// Окно описанного периода в UTC. <see cref="StartUtc"/> = <c>null</c> — начало не названо: «с последнего
/// известного момента» (решает применение, #6).
/// </summary>
public sealed record PeriodWindow(DateTime? StartUtc, DateTime EndUtc);

/// <summary>Почему окно не построить.</summary>
public enum WindowError
{
    /// <summary>Период целиком в будущем — это план, а не факт (spec §5.6).</summary>
    Future,

    /// <summary>У прошлого дня названо только начало — непонятно, до скольки.</summary>
    MissingEnd,
}

/// <summary>Окно или ошибка.</summary>
public sealed record WindowResult(PeriodWindow? Window, WindowError? Error)
{
    public static WindowResult Ok(DateTime? start, DateTime end) => new(new PeriodWindow(start, end), null);

    public static WindowResult Fail(WindowError error) => new(null, error);
}

/// <summary>
/// Перевод сказанного («вчера», «вечером», «с 23 до 1», «в 14») в UTC — кодом, не моделью (spec §5.6, §8).
/// Общий принцип: без явного дня берётся <b>последнее прошедшее</b> вхождение — в 6 утра «вечером» значит вчера
/// вечером, «с 19 до 21» — вчера. Конец в будущем обрезается до «сейчас»; начало в будущем — ошибка.
/// </summary>
public static class PastTime
{
    /// <summary>Части суток в местном времени: утро 06–12, день 12–18, вечер 18–24, ночь 00–06.</summary>
    public static (TimeOnly Start, TimeOnly End) Bounds(PartOfDay part) => PartsOfDay.Bounds(part);

    public static DateOnly ResolveDate(DayRef day, DateOnly today) => day switch
    {
        DayRef.DaysAgo d => today.AddDays(-d.Days),
        DayRef.Weekday w => today.AddDays(-(((int)today.DayOfWeek - (int)w.Day + 7) % 7)),
        DayRef.Date d => d.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(day)),
    };

    /// <summary>Момент «в 14», «вчера в 23». Без дня — последнее прошедшее; <c>null</c> — момент в будущем.</summary>
    public static DateTime? ResolveInstant(DayRef? day, TimeOnly at, DateTime nowUtc, TimeZoneInfo timeZone)
    {
        var today = Today(nowUtc, timeZone);
        var date = day is null ? today : ResolveDate(day, today);
        var instant = ToUtc(date, at, timeZone);
        if (day is null && instant > nowUtc)
        {
            instant = ToUtc(date.AddDays(-1), at, timeZone);
        }

        return instant > nowUtc ? null : instant;
    }

    /// <summary>Окно периода по дню, «с … до …» и части суток.</summary>
    public static WindowResult ResolveWindow(
        DayRef? day, TimeOnly? from, TimeOnly? to, PartOfDay? part, DateTime nowUtc, TimeZoneInfo timeZone)
    {
        var today = Today(nowUtc, timeZone);
        // Часть суток дополняет недостающие границы: «вчера с 18 … весь вечер» — с 18 до конца вечера.
        if (part is { } p)
        {
            var (partStart, partEnd) = Bounds(p);
            from ??= partStart;
            to ??= partEnd;
        }

        // Ничего не названо: «вчера» — весь тот день, иначе — с последнего известного до сейчас.
        if (from is null && to is null)
        {
            if (day is null || ResolveDate(day, today) == today)
            {
                return WindowResult.Ok(null, nowUtc);
            }

            var date = ResolveDate(day, today);
            return Clip(ToUtc(date, TimeOnly.MinValue, timeZone), ToUtc(date.AddDays(1), TimeOnly.MinValue, timeZone), nowUtc);
        }

        // Только конец: «до 15 работал» — начало решит применение.
        if (from is null)
        {
            var end = ResolveInstant(day, to!.Value, nowUtc, timeZone);
            return end is null ? WindowResult.Fail(WindowError.Future) : WindowResult.Ok(null, end.Value);
        }

        var baseDate = day is null ? today : ResolveDate(day, today);
        var start = ToUtc(baseDate, from.Value, timeZone);
        if (to is null)
        {
            // Только начало: «с 19 смотрю сериал» — до сейчас, если это сегодня (или ночью — со вчера).
            if (day is not null && baseDate != today)
            {
                return WindowResult.Fail(WindowError.MissingEnd);
            }

            if (start > nowUtc)
            {
                start = ToUtc(baseDate.AddDays(-1), from.Value, timeZone);
            }

            return Clip(start, nowUtc, nowUtc);
        }

        var endDate = to.Value <= from.Value ? baseDate.AddDays(1) : baseDate;
        var endUtc = ToUtc(endDate, to.Value, timeZone);
        if (day is null && start > nowUtc)
        {
            start = ToUtc(baseDate.AddDays(-1), from.Value, timeZone);
            endUtc = ToUtc(endDate.AddDays(-1), to.Value, timeZone);
        }

        return Clip(start, endUtc, nowUtc);
    }

    /// <summary>
    /// Время пункта («тренировка с 19 до 20») внутри окна: ближайшее к окну вхождение этого местного времени.
    /// <c>null</c> — такого времени в окне нет.
    /// </summary>
    public static DateTime? ResolveInWindow(TimeOnly time, DateTime windowStartUtc, DateTime windowEndUtc, TimeZoneInfo timeZone)
    {
        var first = DateOnly.FromDateTime(LocalTime.ToLocal(windowStartUtc, timeZone));
        var last = DateOnly.FromDateTime(LocalTime.ToLocal(windowEndUtc, timeZone));
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var candidate = ToUtc(date, time, timeZone);
            if (candidate >= windowStartUtc && candidate <= windowEndUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    private static WindowResult Clip(DateTime start, DateTime end, DateTime nowUtc)
    {
        if (start >= nowUtc)
        {
            return WindowResult.Fail(WindowError.Future);
        }

        return WindowResult.Ok(start, end > nowUtc ? nowUtc : end);
    }

    private static DateOnly Today(DateTime nowUtc, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(LocalTime.ToLocal(nowUtc, timeZone));

    private static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone) =>
        LocalTime.ToUtc(date.ToDateTime(time), timeZone);
}
