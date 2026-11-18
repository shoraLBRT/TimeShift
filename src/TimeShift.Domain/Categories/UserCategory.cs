namespace TimeShift.Domain.Categories;

/// <summary>
/// Категория, которую завёл пользователь (spec §4.1, §7: categories). Системные категории в таблице не лежат —
/// они заданы в <see cref="DefaultCategories"/>. Ключ выдаётся один раз и не меняется: на него ссылаются отрезки,
/// занятия и блоки. Удаление — это архив (<see cref="ArchivedAtUtc"/>): старые отрезки по-прежнему знают имя
/// и родителя категории, а в выборе её больше нет.
/// </summary>
public sealed class UserCategory
{
    public const int MaxNameLength = 40;

    /// <summary>Префикс ключей пользовательских категорий; у системных такого нет.</summary>
    public const string KeyPrefix = "u.";

    internal UserCategory(string name, CategoryGroup group, string? parentKey, DateTime createdAtUtc)
    {
        Id = Guid.NewGuid();
        Key = KeyPrefix + Id.ToString("N")[..10];
        Name = name;
        Group = group;
        ParentKey = parentKey;
        CreatedAtUtc = createdAtUtc;
    }

    // Для EF Core.
    private UserCategory()
    {
        Key = string.Empty;
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; }

    public string Name { get; internal set; }

    /// <summary>Группа; у подвида — всегда группа родителя.</summary>
    public CategoryGroup Group { get; internal set; }

    /// <summary>Ключ родительской категории (системной или своей) для подвида.</summary>
    public string? ParentKey { get; internal set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Когда удалена; <c>null</c> — действует.</summary>
    public DateTime? ArchivedAtUtc { get; internal set; }

    public bool IsArchived => ArchivedAtUtc is not null;

    public Category ToCategory() => new(Key, Name, Group, ParentKey);
}

/// <summary>
/// Правка набора категорий нарушает правило. <see cref="Field"/> — что не так: <c>name</c>, <c>group</c>,
/// <c>parent</c>; <see cref="UnknownKey"/> — такой своей категории нет; <see cref="SystemKey"/> — категория системная.
/// </summary>
public sealed class CategoryException(string field, string message) : Exception(message)
{
    public const string UnknownKey = "key";

    public const string SystemKey = "system";

    public string Field { get; } = field;
}
