namespace TimeShift.Domain.Timelines;

/// <summary>
/// Кусок построенной ленты <c>[StartUtc, EndUtc)</c>. <see cref="IsFuture"/> — кусок после «сейчас»
/// (прогноз по шаблону), он не является фактом.
/// </summary>
public sealed record TimelinePiece(
    DateTime StartUtc,
    DateTime EndUtc,
    string Title,
    Guid? ActivityId,
    string? CategoryKey,
    SegmentStatus Status,
    SegmentSource Source,
    bool IsFuture)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>Совпадает всё, кроме границ, — такие соседние куски сливаются.</summary>
    public bool HasSameContent(TimelinePiece other) =>
        Title == other.Title
        && ActivityId == other.ActivityId
        && CategoryKey == other.CategoryKey
        && Status == other.Status
        && Source == other.Source
        && IsFuture == other.IsFuture;
}
