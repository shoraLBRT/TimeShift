namespace TimeShift.Domain.Categories;

/// <summary>
/// Набор категорий одного пользователя (spec §4.1): системные из <see cref="DefaultCategories"/> и его собственные.
/// Удалённые (архивные) категории остаются в наборе для имён и итогов по старым отрезкам, но выбрать их нельзя.
/// </summary>
public sealed class CategorySet
{
    private readonly Dictionary<string, Category> _all;
    private readonly HashSet<string> _archived;

    public CategorySet(IEnumerable<UserCategory> own)
    {
        ArgumentNullException.ThrowIfNull(own);
        var custom = own.OrderBy(c => c.CreatedAtUtc).ToList();
        _archived = custom.Where(c => c.IsArchived).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        All = [.. DefaultCategories.All, .. custom.Select(c => c.ToCategory())];
        _all = All.ToDictionary(c => c.Key, StringComparer.Ordinal);
        Active = All.Where(c => !_archived.Contains(c.Key)).ToList();
    }

    /// <summary>Только системный набор — когда пользователя нет (проверки контракта, тесты).</summary>
    public static CategorySet System { get; } = new([]);

    /// <summary>Все категории, включая удалённые: системные в своём порядке, затем свои по времени создания.</summary>
    public IReadOnlyList<Category> All { get; }

    /// <summary>Категории, которые можно выбрать.</summary>
    public IReadOnlyList<Category> Active { get; }

    /// <summary>Категория по ключу, в том числе удалённая — чтобы назвать её в старых отрезках.</summary>
    public Category? Find(string? key) => key is not null && _all.TryGetValue(key, out var category) ? category : null;

    /// <summary>Категория, которую можно выбрать для новой записи.</summary>
    public Category? FindActive(string? key) => Find(key) is { } category && !_archived.Contains(category.Key) ? category : null;

    public bool IsArchived(string key) => _archived.Contains(key);

    public static bool IsSystem(string key) => DefaultCategories.Find(key) is not null;

    /// <summary>Имя категории; неизвестный ключ возвращается как есть.</summary>
    public string Name(string key) => Find(key)?.Name ?? key;

    /// <summary>Ключ, под которым категория входит в итоги: родитель подвида или она сама.</summary>
    public string? TotalsKey(string? key) => Find(key)?.ParentKey ?? key;

    /// <summary>Действующая категория по имени, без учёта регистра и «ё».</summary>
    public Category? FindByName(string name)
    {
        var wanted = Normalize(name);
        return Active.FirstOrDefault(c => Normalize(c.Name) == wanted);
    }

    public static string Normalize(string name) => name.Trim().ToLowerInvariant().Replace('ё', 'е');
}
