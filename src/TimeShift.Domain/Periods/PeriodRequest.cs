using TimeShift.Domain.Timelines;

namespace TimeShift.Domain.Periods;

/// <summary>Входные данные разбора периода (<see cref="PeriodResolver.Resolve"/>).</summary>
public sealed class PeriodRequest
{
    /// <summary>Окно <c>[A, B]</c>: A — последний известный момент, B — «сейчас» или явная граница.</summary>
    public required TimeRange Window { get; init; }

    public required IReadOnlyList<PeriodItem> Items { get; init; }

    /// <summary>Факты пользователя: ключ → минуты.</summary>
    public IReadOnlyDictionary<string, int> Facts { get; init; } = new Dictionary<string, int>();

    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Шаг округления границ в минутах (5 / 10 / 15).</summary>
    public int RoundingMinutes { get; init; } = 5;
}
