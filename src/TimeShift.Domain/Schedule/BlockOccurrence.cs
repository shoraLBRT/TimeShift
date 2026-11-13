using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Schedule;

/// <summary>Конкретное вхождение блока шаблона, переведённое в UTC.</summary>
public sealed record BlockOccurrence(ScheduleBlock Block, TimeRange Range);
