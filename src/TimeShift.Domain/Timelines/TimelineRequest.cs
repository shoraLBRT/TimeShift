using TimeShift.Domain.Schedule;

namespace TimeShift.Domain.Timelines;

/// <summary>Входные данные построения ленты (<see cref="TimelineBuilder.Build"/>).</summary>
public sealed class TimelineRequest
{
    /// <summary>Окно <c>[from, to)</c>, которое лента покрывает целиком.</summary>
    public required TimeRange Window { get; init; }

    public required DateTime NowUtc { get; init; }

    public required WeekTemplate Template { get; init; }

    /// <summary>Материализованные закрытые отрезки, не пересекаются. Открытый отрезок передаётся через <see cref="OpenActivity"/>.</summary>
    public IReadOnlyList<Segment> Segments { get; init; } = [];

    public OpenActivity? OpenActivity { get; init; }

    public ExtensionPolicy Policy { get; init; } = ExtensionPolicy.Default;

    /// <summary>Подставляется вместо неизвестной типичной длительности; всё продление тогда 🟡.</summary>
    public TimeSpan UnknownDurationFallback { get; init; } = TimelineBuilder.DefaultUnknownDurationFallback;
}
