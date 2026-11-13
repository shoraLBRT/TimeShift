namespace TimeShift.Domain.Timelines;

/// <summary>Откуда взялся отрезок (spec §7: segments.source).</summary>
public enum SegmentSource
{
    User,
    Extension,
    Schedule,
    Background,
    Import,
}
