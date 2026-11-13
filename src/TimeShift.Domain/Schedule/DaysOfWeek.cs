namespace TimeShift.Domain.Schedule;

/// <summary>Готовые наборы дней недели для блоков шаблона.</summary>
public static class DaysOfWeek
{
    public static IReadOnlyList<DayOfWeek> All { get; } = Enum.GetValues<DayOfWeek>();

    public static IReadOnlyList<DayOfWeek> Weekdays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public static IReadOnlyList<DayOfWeek> Weekend { get; } = [DayOfWeek.Saturday, DayOfWeek.Sunday];
}
