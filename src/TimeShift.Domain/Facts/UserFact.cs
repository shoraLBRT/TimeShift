namespace TimeShift.Domain.Facts;

/// <summary>
/// Факт пользователя в минутах (spec §5.5, §7): длительность маршрута «зал → дом» и т. п. Ключ —
/// <see cref="RouteKey"/>; разбор периода (spec §3.5) берёт длительности по нему.
/// </summary>
public class UserFact
{
    public UserFact(Guid userId, string key, int minutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        UserId = userId;
        Key = key;
        Minutes = minutes;
    }

    // Для EF Core.
    private UserFact()
    {
        Key = string.Empty;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public string Key { get; private set; }

    public int Minutes
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }

    /// <summary>Ключ маршрута: <c>route:зал→дом</c>, без регистра и «ё».</summary>
    public static string RouteKey(string from, string to) => $"route:{Normalize(from)}→{Normalize(to)}";

    /// <summary>Места из ключа маршрута, если это маршрут.</summary>
    public static (string From, string To)? ParseRouteKey(string key)
    {
        if (!key.StartsWith("route:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = key["route:".Length..].Split('→');
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    private static string Normalize(string place) => place.Trim().ToLowerInvariant().Replace('ё', 'е');
}
