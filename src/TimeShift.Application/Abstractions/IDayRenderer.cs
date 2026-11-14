using TimeShift.Domain.Timelines;

namespace TimeShift.Application.Abstractions;

/// <summary>Что нарисовать: лента одного дня (spec §5.7: /day картинкой).</summary>
public sealed record DayImage(
    string Title,
    DateTime DayStartUtc,
    DateTime DayEndUtc,
    DateTime NowUtc,
    TimeZoneInfo TimeZone,
    IReadOnlyList<TimelinePiece> Pieces,
    IReadOnlyList<TimelinePiece> Parallel);

/// <summary>Неделя по дням (spec §5.7: /week): для каждого дня — куски основной дорожки.</summary>
public sealed record WeekImage(string Title, TimeZoneInfo TimeZone, IReadOnlyList<(DateOnly Day, DateTime StartUtc, IReadOnlyList<TimelinePiece> Pieces)> Days);

/// <summary>Картинки лент. Нет реализации или не вышло — <c>null</c>, бот ответит текстом.</summary>
public interface IDayRenderer
{
    /// <summary>PNG.</summary>
    byte[]? RenderDay(DayImage day);

    /// <summary>PNG.</summary>
    byte[]? RenderWeek(WeekImage week);
}
