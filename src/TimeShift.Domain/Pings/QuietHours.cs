using TimeShift.Domain.Schedule;

namespace TimeShift.Domain.Pings;

/// <summary>
/// Ежедневные часы без пингов в местном времени пользователя (spec §5.2): с <paramref name="Start"/> до
/// <paramref name="End"/>, конец не включён; <c>End &lt; Start</c> — через полночь.
/// </summary>
public sealed record QuietHours
{
    public QuietHours(TimeOnly start, TimeOnly end)
    {
        if (start == end)
        {
            throw new ArgumentException("Начало и конец часов тишины не могут совпадать.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    /// <summary>Конец часов тишины, если <paramref name="utc"/> попадает в них; иначе <c>null</c>.</summary>
    public DateTime? EndIfInside(DateTime utc, TimeZoneInfo timeZone)
    {
        var local = LocalTime.ToLocal(utc, timeZone);
        var time = TimeOnly.FromDateTime(local);
        var date = DateOnly.FromDateTime(local);
        DateOnly endDate;
        if (Start < End)
        {
            if (time < Start || time >= End)
            {
                return null;
            }

            endDate = date;
        }
        else if (time >= Start)
        {
            endDate = date.AddDays(1);
        }
        else if (time < End)
        {
            endDate = date;
        }
        else
        {
            return null;
        }

        var end = LocalTime.ToUtc(endDate.ToDateTime(End), timeZone);

        // Перевод часов может «съесть» конец — двигаемся хотя бы на минуту, чтобы не зациклиться.
        return end > utc ? end : utc.AddMinutes(1);
    }
}
