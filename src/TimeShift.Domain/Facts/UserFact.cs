using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;

namespace TimeShift.Domain.Facts;

/// <summary>
/// Факт пользователя (spec §4.2, §5.5, §7). Либо длительность в минутах — маршрут «зал → дом», ключ
/// <see cref="RouteKey"/>, по нему разбор периода (spec §3.5) берёт длительности; либо правило классификации —
/// ключ <see cref="RuleKey"/>, значение — ключ категории.
/// </summary>
public class UserFact
{
    private const string RoutePrefix = "route:";

    private const string RulePrefix = "rule:";

    public UserFact(Guid userId, string key, int minutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        UserId = userId;
        Key = key;
        Minutes = minutes;
    }

    /// <summary>Правило классификации как факт.</summary>
    public UserFact(Guid userId, CategoryRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.CategoryKey);
        UserId = userId;
        Key = RuleKey(rule.ActivityId, rule.Part);
        Value = rule.CategoryKey;
    }

    // Для EF Core.
    private UserFact()
    {
        Key = string.Empty;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public string Key { get; private set; }

    /// <summary>Минуты факта-длительности; у правила классификации — <c>null</c>.</summary>
    public int? Minutes
    {
        get;
        set
        {
            if (value is { } minutes)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minutes);
            }

            field = value;
        }
    }

    /// <summary>Значение факта-правила — ключ категории; у длительности — <c>null</c>.</summary>
    public string? Value { get; set; }

    /// <summary>Ключ маршрута: <c>route:зал→дом</c>, без регистра и «ё».</summary>
    public static string RouteKey(string from, string to) => $"{RoutePrefix}{Normalize(from)}→{Normalize(to)}";

    /// <summary>Места из ключа маршрута, если это маршрут.</summary>
    public static (string From, string To)? ParseRouteKey(string key)
    {
        if (!key.StartsWith(RoutePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = key[RoutePrefix.Length..].Split('→');
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    /// <summary>Ключ правила классификации: <c>rule:&lt;id занятия&gt;:evening</c> — одно правило на занятие и часть суток.</summary>
    public static string RuleKey(Guid activityId, PartOfDay part) => $"{RulePrefix}{activityId:N}:{PartsOfDay.Code(part)}";

    /// <summary>Правило классификации, если этот факт — оно.</summary>
    public CategoryRule? AsRule()
    {
        if (Value is null || !Key.StartsWith(RulePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = Key[RulePrefix.Length..].Split(':');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var activityId) && PartsOfDay.Parse(parts[1]) is { } part
            ? new CategoryRule(activityId, part, Value)
            : null;
    }

    private static string Normalize(string place) => place.Trim().ToLowerInvariant().Replace('ё', 'е');
}
