namespace TimeShift.Domain.Periods;

/// <summary>Пункт описания периода (из намерения <c>describe_period</c>, spec §8).</summary>
public sealed record PeriodItem
{
    public PeriodItem(string title, DurationSpec duration, string? categoryHint = null, Guid? activityId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(duration);
        Title = title;
        Duration = duration;
        CategoryHint = categoryHint;
        ActivityId = activityId;
    }

    public string Title { get; }

    public DurationSpec Duration { get; }

    /// <summary>Ключ категории, если он известен; <c>null</c> — бот спросит.</summary>
    public string? CategoryHint { get; }

    public Guid? ActivityId { get; }
}
