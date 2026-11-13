namespace TimeShift.Domain.Timelines;

/// <summary>
/// Открытое занятие: о начале сказано, о конце — ещё нет (spec §2, §3.3).
/// <paramref name="TypicalDuration"/> = <c>null</c> — типичная длительность неизвестна.
/// </summary>
public sealed record OpenActivity(
    DateTime StartUtc,
    string Title,
    TimeSpan? TypicalDuration,
    Guid? ActivityId = null,
    string? CategoryKey = null)
{
    /// <summary>Открытое занятие из открытого материализованного отрезка.</summary>
    public static OpenActivity FromSegment(Segment segment, TimeSpan? typicalDuration)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!segment.IsOpen)
        {
            throw new ArgumentException("Отрезок не открыт.", nameof(segment));
        }

        return new OpenActivity(segment.StartUtc, segment.Title, typicalDuration, segment.ActivityId, segment.CategoryKey);
    }
}
