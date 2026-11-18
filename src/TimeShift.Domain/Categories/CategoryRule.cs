using TimeShift.Domain.Activities;
using TimeShift.Domain.Schedule;

namespace TimeShift.Domain.Categories;

/// <summary>
/// Правило классификации пользователя (spec §4.2): занятие в такую-то часть суток относится к такой-то категории —
/// «YouTube по вечерам — отдых». Условие одно — часть суток (<see cref="PartsOfDay"/>), у занятия не больше одного
/// правила на каждую.
/// </summary>
public sealed record CategoryRule(Guid ActivityId, PartOfDay Part, string CategoryKey);

/// <summary>Правила классификации одного пользователя и выбор категории для нового отрезка.</summary>
public sealed class CategoryRules
{
    private readonly Dictionary<(Guid ActivityId, PartOfDay Part), CategoryRule> _rules;

    public CategoryRules(IEnumerable<CategoryRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [];
        foreach (var rule in rules)
        {
            _rules[(rule.ActivityId, rule.Part)] = rule;
        }
    }

    public static CategoryRules None { get; } = new([]);

    public IReadOnlyCollection<CategoryRule> All => _rules.Values;

    /// <summary>Правила одного занятия, от утра к ночи.</summary>
    public IReadOnlyList<CategoryRule> Of(Guid activityId) =>
        _rules.Values.Where(r => r.ActivityId == activityId).OrderBy(r => r.Part).ToList();

    public CategoryRule? Find(Guid activityId, PartOfDay part) => _rules.GetValueOrDefault((activityId, part));

    /// <summary>
    /// Категория по правилу для отрезка занятия, начавшегося в <paramref name="startUtc"/>: часть суток берётся
    /// по началу отрезка в часовом поясе пользователя. <c>null</c> — правила на это время нет или его категории
    /// больше нет в выборе.
    /// </summary>
    public string? Match(Guid activityId, DateTime startUtc, TimeZoneInfo timeZone, CategorySet categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return Find(activityId, PartsOfDay.Of(startUtc, timeZone)) is { } rule ? categories.FindActive(rule.CategoryKey)?.Key : null;
    }

    /// <summary>
    /// Категория нового отрезка занятия, если она не названа явно: правило на это время суток, иначе — категория
    /// занятия (в неё при заведении занятия попадает и догадка <see cref="Classifier"/>).
    /// </summary>
    public string? Resolve(Activity activity, DateTime startUtc, TimeZoneInfo timeZone, CategorySet categories)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return Match(activity.Id, startUtc, timeZone, categories) ?? activity.CategoryKey;
    }
}
