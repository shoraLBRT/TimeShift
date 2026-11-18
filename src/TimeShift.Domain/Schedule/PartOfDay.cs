namespace TimeShift.Domain.Schedule;

/// <summary>Часть суток в местном времени пользователя: «утром», «днём», «вечером», «ночью».</summary>
public enum PartOfDay
{
    Morning,
    Afternoon,
    Evening,
    Night,
}

/// <summary>
/// Границы частей суток — одни на всё приложение: утро 06–12, день 12–18, вечер 18–24, ночь 00–06 (местное время).
/// По ним разбирается «вечером» в тексте (spec §5.6) и срабатывают правила классификации (spec §4.2).
/// </summary>
public static class PartsOfDay
{
    public static IReadOnlyList<PartOfDay> All { get; } = [PartOfDay.Morning, PartOfDay.Afternoon, PartOfDay.Evening, PartOfDay.Night];

    /// <summary>Начало и конец части суток; конец вечера — полночь (<see cref="TimeOnly.MinValue"/>).</summary>
    public static (TimeOnly Start, TimeOnly End) Bounds(PartOfDay part) => part switch
    {
        PartOfDay.Morning => (new TimeOnly(6, 0), new TimeOnly(12, 0)),
        PartOfDay.Afternoon => (new TimeOnly(12, 0), new TimeOnly(18, 0)),
        PartOfDay.Evening => (new TimeOnly(18, 0), TimeOnly.MinValue),
        PartOfDay.Night => (TimeOnly.MinValue, new TimeOnly(6, 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    public static PartOfDay Of(TimeOnly local) => local.Hour switch
    {
        < 6 => PartOfDay.Night,
        < 12 => PartOfDay.Morning,
        < 18 => PartOfDay.Afternoon,
        _ => PartOfDay.Evening,
    };

    /// <summary>Часть суток, на которую приходится момент, в часовом поясе пользователя.</summary>
    public static PartOfDay Of(DateTime utc, TimeZoneInfo timeZone) => Of(TimeOnly.FromDateTime(LocalTime.ToLocal(utc, timeZone)));

    /// <summary>Код для хранения и контрактов (REST, LLM): <c>morning</c>, <c>afternoon</c>, <c>evening</c>, <c>night</c>.</summary>
    public static string Code(PartOfDay part) => part switch
    {
        PartOfDay.Morning => "morning",
        PartOfDay.Afternoon => "afternoon",
        PartOfDay.Evening => "evening",
        PartOfDay.Night => "night",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    public static PartOfDay? Parse(string? code) => code switch
    {
        "morning" => PartOfDay.Morning,
        "afternoon" => PartOfDay.Afternoon,
        "evening" => PartOfDay.Evening,
        "night" => PartOfDay.Night,
        _ => null,
    };

    /// <summary>«утром», «днём», «вечером», «ночью».</summary>
    public static string When(PartOfDay part) => part switch
    {
        PartOfDay.Morning => "утром",
        PartOfDay.Afternoon => "днём",
        PartOfDay.Evening => "вечером",
        PartOfDay.Night => "ночью",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>Часы части суток для показа: «18:00–24:00».</summary>
    public static string Hours(PartOfDay part) => part switch
    {
        PartOfDay.Morning => "06:00–12:00",
        PartOfDay.Afternoon => "12:00–18:00",
        PartOfDay.Evening => "18:00–24:00",
        PartOfDay.Night => "00:00–06:00",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>«вечером (18:00–24:00)» — так часть суток называется везде, где правило создаётся или показывается.</summary>
    public static string Label(PartOfDay part) => $"{When(part)} ({Hours(part)})";
}
