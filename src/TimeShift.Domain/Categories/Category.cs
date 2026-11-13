namespace TimeShift.Domain.Categories;

/// <summary>Категория или подвид (если задан <paramref name="ParentKey"/>) для статистики (spec §4.1).</summary>
/// <param name="Key">Стабильный строковый ключ, например <c>sleep</c>, <c>wasted.short_video</c>.</param>
/// <param name="Name">Отображаемое имя.</param>
/// <param name="Group">Группа категорий.</param>
/// <param name="ParentKey">Ключ родительской категории для подвида.</param>
public sealed record Category(string Key, string Name, CategoryGroup Group, string? ParentKey = null);
