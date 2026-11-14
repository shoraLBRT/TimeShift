using TimeShift.Domain.Categories;

namespace TimeShift.Domain.Schedule;

/// <summary>
/// Повторяющийся блок шаблона недели в местном времени пользователя (spec §2, §7: schedule_blocks).
/// <see cref="End"/> &lt; <see cref="Start"/> — блок переходит через полночь (например, сон 23:30–07:30).
/// Дни недели — это дни, в которые блок <b>начинается</b>.
/// </summary>
public sealed class ScheduleBlock
{
    public const string SleepTitle = "Сон";

    public ScheduleBlock(
        ScheduleBlockKind kind,
        string title,
        string? categoryKey,
        IEnumerable<DayOfWeek> days,
        TimeOnly start,
        TimeOnly end,
        Guid? activityId = null,
        Guid? id = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(days);
        var daySet = days.ToHashSet();
        if (daySet.Count == 0 || daySet.Any(d => !Enum.IsDefined(d)))
        {
            throw new ArgumentException("Нужен хотя бы один корректный день недели.", nameof(days));
        }

        if (start == end)
        {
            throw new ArgumentException("Начало и конец блока совпадают.", nameof(end));
        }

        Id = id ?? Guid.NewGuid();
        Kind = kind;
        Title = title;
        CategoryKey = categoryKey;
        Days = daySet;
        Start = start;
        End = end;
        ActivityId = activityId;
    }

    // Для EF Core.
    private ScheduleBlock()
    {
        Title = string.Empty;
        Days = new HashSet<DayOfWeek>();
    }

    public Guid Id { get; }

    public ScheduleBlockKind Kind { get; }

    public string Title { get; }

    public string? CategoryKey { get; }

    public Guid? ActivityId { get; }

    /// <summary>Дни, в которые блок начинается.</summary>
    public IReadOnlySet<DayOfWeek> Days { get; }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    public bool CrossesMidnight => End < Start;

    /// <summary>Длительность по местным часам (без учёта перевода часов).</summary>
    public TimeSpan Length => End - Start;

    /// <summary>Блок «Сон» с категорией <c>sleep</c>.</summary>
    public static ScheduleBlock Sleep(IEnumerable<DayOfWeek> days, TimeOnly start, TimeOnly end) =>
        new(ScheduleBlockKind.Sleep, SleepTitle, DefaultCategories.Sleep, days, start, end);

    /// <summary>
    /// Дни начала сна для ночей перед буднями (<paramref name="beforeWeekend"/> = false: подъём пн–пт) или перед
    /// выходными (подъём сб–вс). Отбой с 12:00 считается вечерним — блок начинается накануне дня подъёма.
    /// </summary>
    public static IReadOnlyList<DayOfWeek> SleepDays(TimeOnly bedtime, bool beforeWeekend)
    {
        var wakeDays = beforeWeekend ? DaysOfWeek.Weekend : DaysOfWeek.Weekdays;
        return bedtime.Hour >= 12
            ? wakeDays.Select(d => (DayOfWeek)(((int)d + 6) % 7)).ToList()
            : wakeDays;
    }

    public override string ToString() =>
        $"{Kind} '{Title}' {Start:HH\\:mm}-{End:HH\\:mm} [{string.Join(",", Days.Order())}]";
}
