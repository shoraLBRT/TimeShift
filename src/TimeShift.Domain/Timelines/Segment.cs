namespace TimeShift.Domain.Timelines;

/// <summary>
/// Материализованный отрезок ленты <c>[StartUtc, EndUtc)</c> (spec §2, §7: segments).
/// <see cref="EndUtc"/> = <c>null</c> — открытое занятие. Неизменяемый: изменения — через новые экземпляры.
/// </summary>
public sealed record Segment
{
    public Segment(
        DateTime startUtc,
        DateTime? endUtc,
        string title,
        SegmentStatus status,
        SegmentSource source,
        Guid? activityId = null,
        string? categoryKey = null)
    {
        Utc.Ensure(startUtc, nameof(startUtc));
        if (endUtc is { } end)
        {
            Utc.Ensure(end, nameof(endUtc));
            if (end <= startUtc)
            {
                throw new ArgumentException("Конец отрезка должен быть позже начала.", nameof(endUtc));
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        StartUtc = startUtc;
        EndUtc = endUtc;
        Title = title;
        Status = status;
        Source = source;
        ActivityId = activityId;
        CategoryKey = categoryKey;
    }

    public DateTime StartUtc { get; }

    /// <summary><c>null</c> — открытое занятие.</summary>
    public DateTime? EndUtc { get; }

    public string Title { get; }

    public SegmentStatus Status { get; }

    public SegmentSource Source { get; }

    public Guid? ActivityId { get; }

    /// <summary>Ключ категории (например, <c>sport</c>, <c>wasted.youtube</c>); <c>null</c> — не определена.</summary>
    public string? CategoryKey { get; }

    public bool IsOpen => EndUtc is null;

    /// <summary>Конец для расчёта пересечений: открытый отрезок тянется в бесконечность.</summary>
    internal DateTime EffectiveEndUtc => EndUtc ?? DateTime.MaxValue;

    public Segment WithRange(DateTime startUtc, DateTime? endUtc) =>
        new(startUtc, endUtc, Title, Status, Source, ActivityId, CategoryKey);

    internal bool Overlaps(Segment other) =>
        StartUtc < other.EffectiveEndUtc && other.StartUtc < EffectiveEndUtc;
}
