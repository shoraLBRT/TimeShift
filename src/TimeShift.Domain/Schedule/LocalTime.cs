namespace TimeShift.Domain.Schedule;

/// <summary>
/// Перевод между местным временем пользователя и UTC с однозначными правилами для перевода часов:
/// несуществующее местное время (перевод вперёд) → момент перехода; неоднозначное (перевод назад) → более раннее
/// из двух мгновений. Это наименьший момент UTC, чьё местное время не раньше заданного, поэтому отображение
/// монотонно: непересекающиеся местные интервалы остаются непересекающимися в UTC.
/// </summary>
public static class LocalTime
{
    public static DateTime ToLocal(DateTime utc, TimeZoneInfo timeZone)
    {
        Utc.Ensure(utc, nameof(utc));
        ArgumentNullException.ThrowIfNull(timeZone);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone), DateTimeKind.Unspecified);
    }

    public static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(local))
        {
            return TransitionInstant(local, timeZone);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    /// <summary>Наименьший момент UTC, чьё местное время ≥ <paramref name="local"/> (внутри «дыры» перевода часов).</summary>
    private static DateTime TransitionInstant(DateTime local, TimeZoneInfo timeZone)
    {
        // Смещения лежат в [-12h, +14h], поэтому f(lo) < local <= f(hi); около «дыры» f монотонна.
        var lo = local.Ticks - TimeSpan.FromHours(15).Ticks;
        var hi = local.Ticks + TimeSpan.FromHours(15).Ticks;
        while (hi - lo > 1)
        {
            var mid = lo + ((hi - lo) / 2);
            var offset = timeZone.GetUtcOffset(new DateTime(mid, DateTimeKind.Utc));
            if (mid + offset.Ticks >= local.Ticks)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return new DateTime(hi, DateTimeKind.Utc);
    }
}
