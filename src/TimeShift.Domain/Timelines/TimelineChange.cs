namespace TimeShift.Domain.Timelines;

/// <summary>
/// Результат <see cref="Timeline.Apply"/>: какие отрезки убраны (<see cref="Before"/>, в исходном виде) и какие
/// добавлены (<see cref="After"/>: обрезки старых + входящие). Достаточно для <c>/undo</c> (spec §7: timeline_changes).
/// </summary>
public sealed record TimelineChange(IReadOnlyList<Segment> Before, IReadOnlyList<Segment> After)
{
    public bool IsEmpty => Before.Count == 0 && After.Count == 0;
}
