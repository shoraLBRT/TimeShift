using System.Globalization;

namespace TimeShift.Application.Parsing;

/// <summary>
/// День из текста команды (spec §5.6, §5.7): «сегодня», «вчера», «позавчера», день недели («сб», «субботу» —
/// последний прошедший, сегодня тоже подходит), «13.11» или «13.11.2026». Будущее — ошибка.
/// </summary>
public static class DayArgument
{
    private static readonly (string Prefix, DayOfWeek Day)[] Weekdays =
    [
        ("пн", DayOfWeek.Monday), ("пон", DayOfWeek.Monday), ("вт", DayOfWeek.Tuesday), ("вто", DayOfWeek.Tuesday),
        ("ср", DayOfWeek.Wednesday), ("сре", DayOfWeek.Wednesday), ("чт", DayOfWeek.Thursday), ("чет", DayOfWeek.Thursday),
        ("пт", DayOfWeek.Friday), ("пят", DayOfWeek.Friday), ("сб", DayOfWeek.Saturday), ("суб", DayOfWeek.Saturday),
        ("вс", DayOfWeek.Sunday), ("вос", DayOfWeek.Sunday),
    ];

    /// <summary>Разбирает день; <paramref name="error"/> — понятное объяснение, если не вышло.</summary>
    public static bool TryParse(string text, DateOnly today, out DateOnly date, out string? error)
    {
        date = today;
        error = null;
        var t = text.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (t.StartsWith("в ", StringComparison.Ordinal) || t.StartsWith("во ", StringComparison.Ordinal))
        {
            t = t[(t.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        }

        switch (t)
        {
            case "" or "сегодня":
                return true;
            case "вчера":
                date = today.AddDays(-1);
                return true;
            case "позавчера":
                date = today.AddDays(-2);
                return true;
        }

        if (TryDate(t, today, out var parsed))
        {
            if (parsed > today)
            {
                error = "Это ещё не наступило — лента бывает только у прошедших дней.";
                return false;
            }

            date = parsed;
            return true;
        }

        foreach (var (prefix, day) in Weekdays)
        {
            if (t == prefix || (prefix.Length == 3 && t.StartsWith(prefix, StringComparison.Ordinal)))
            {
                date = today.AddDays(-(((int)today.DayOfWeek - (int)day + 7) % 7));
                return true;
            }
        }

        error = "Не понял, какой день. Например: /day вчера, /day сб, /day 13.11.";
        return false;
    }

    private static bool TryDate(string text, DateOnly today, out DateOnly date)
    {
        if (DateOnly.TryParseExact(text, ["d.M.yyyy", "dd.MM.yyyy", "d.M.yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        if (DateOnly.TryParseExact($"{text}.{today.Year}", ["d.M.yyyy", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            // «31.12» в январе — прошлогоднее; а дата в ближайший месяц вперёд — это будущее, не прошлый год.
            if (date > today && date.DayNumber - today.DayNumber > 31)
            {
                date = date.AddYears(-1);
            }

            return true;
        }

        return false;
    }
}
