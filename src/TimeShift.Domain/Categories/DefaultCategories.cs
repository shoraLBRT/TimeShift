namespace TimeShift.Domain.Categories;

/// <summary>Стартовый системный набор категорий (spec §4.1). Ключи стабильны — на них ссылаются данные.</summary>
public static class DefaultCategories
{
    public const string Work = "work";
    public const string Study = "study";
    public const string Projects = "projects";
    public const string Sport = "sport";
    public const string Sleep = "sleep";
    public const string Commute = "commute";
    public const string Household = "household";
    public const string Family = "family";
    public const string Friends = "friends";
    public const string MindfulRest = "rest";
    public const string Wasted = "wasted";
    public const string WastedShortVideo = "wasted.short_video";
    public const string WastedYouTube = "wasted.youtube";
    public const string WastedMultiplayer = "wasted.multiplayer";
    public const string WastedLying = "wasted.lying";
    public const string WastedOther = "wasted.other";

    public static IReadOnlyList<Category> All { get; } =
    [
        new(Work, "Работа", CategoryGroup.Useful),
        new(Study, "Учёба", CategoryGroup.Useful),
        new(Projects, "Свои проекты", CategoryGroup.Useful),
        new(Sport, "Спорт", CategoryGroup.Useful),
        new(Sleep, "Сон", CategoryGroup.Necessary),
        new(Commute, "Дорога", CategoryGroup.Necessary),
        new(Household, "Быт", CategoryGroup.Necessary),
        new(Family, "Семья", CategoryGroup.People),
        new(Friends, "Друзья", CategoryGroup.People),
        new(MindfulRest, "Осознанный отдых", CategoryGroup.Rest),
        new(Wasted, "Залипание", CategoryGroup.Lost),
        new(WastedShortVideo, "Соцсети/короткие видео", CategoryGroup.Lost, Wasted),
        new(WastedYouTube, "YouTube", CategoryGroup.Lost, Wasted),
        new(WastedMultiplayer, "Мультиплеер-игры", CategoryGroup.Lost, Wasted),
        new(WastedLying, "Валялся", CategoryGroup.Lost, Wasted),
        new(WastedOther, "Прочее", CategoryGroup.Lost, Wasted),
    ];

    public static Category? Find(string key) => All.FirstOrDefault(c => c.Key == key);

    public static string GroupName(CategoryGroup group) => group switch
    {
        CategoryGroup.Useful => "Полезное",
        CategoryGroup.Necessary => "Необходимое",
        CategoryGroup.People => "Люди",
        CategoryGroup.Rest => "Отдых",
        CategoryGroup.Lost => "Потерянное",
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };
}
