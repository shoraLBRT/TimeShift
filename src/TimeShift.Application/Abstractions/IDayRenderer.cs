using TimeShift.Application.Tracking;
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

/// <summary>День недели на картинке: окно дня и куски основной дорожки (пусто — день не считается: будущий или до регистрации).</summary>
public sealed record WeekDay(DateOnly Day, DateTime StartUtc, DateTime EndUtc, IReadOnlyList<TimelinePiece> Pieces);

/// <summary>Неделя (spec §5.7: /week): полоса на каждый день и цифры — категории, доли статусов, параллельные.</summary>
public sealed record WeekImage(string Title, TimeZoneInfo TimeZone, IReadOnlyList<WeekDay> Days, WeekStats Stats);

/// <summary>Картинки лент. Нет реализации или не вышло — <c>null</c>, бот ответит текстом.</summary>
public interface IDayRenderer
{
    /// <summary>PNG.</summary>
    byte[]? RenderDay(DayImage day);

    /// <summary>PNG.</summary>
    byte[]? RenderWeek(WeekImage week);
}
