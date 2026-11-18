using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;

namespace TimeShift.Application.Clients;

public sealed record SettingsModel(
    int PingIntervalMin,
    double? NormFactor,
    double? MaxFactor,
    int MaxUnansweredPings,
    bool PingDuringBlocks,
    int EveningReviewOffsetMin,
    int RoundingMin);

public sealed record IntRange(int Min, int Max);

public sealed record LimitsModel(
    IntRange PingIntervalMin,
    IntRange MaxUnansweredPings,
    IntRange EveningReviewOffsetMin,
    IReadOnlyList<int> RoundingMin);

/// <summary>Время сна: <paramref name="Start"/> — отбой, <paramref name="End"/> — подъём (местное время).</summary>
public sealed record SleepRange(TimeOnly Start, TimeOnly End);

/// <summary>Сон: каждую ночь одинаково или отдельно перед выходными (<paramref name="Weekend"/>).</summary>
public sealed record SleepModel(TimeOnly Start, TimeOnly End, SleepRange? Weekend);

/// <summary>Блок шаблона (кроме сна). <c>Days</c> — дни ISO (1 = понедельник … 7 = воскресенье), в которые блок начинается.</summary>
public sealed record BlockModel(Guid Id, ScheduleBlockKind Kind, string Title, string? Category, IReadOnlyList<int> Days, TimeOnly Start, TimeOnly End);

/// <summary>Категория для клиента. <c>System</c> — из стартового набора (не правится); <c>Archived</c> — удалена: нужна только чтобы назвать старые записи.</summary>
public sealed record CategoryModel(string Key, string Name, CategoryGroup Group, string? Parent, bool System, bool Archived);

/// <summary>Новая своя категория: <c>Parent</c> — ключ категории верхнего уровня (тогда группа берётся у неё), иначе нужна <c>Group</c>.</summary>
public sealed record CategoryRequest(string? Name, CategoryGroup? Group, string? Parent);

/// <summary>Правка своей категории: что не задано — не меняется; <c>Parent</c> = <c>null</c> делает подвид отдельной категорией.</summary>
public sealed record CategoryPatch
{
    public string? Name { get; init; }

    public CategoryGroup? Group { get; init; }

    public Optional<string?> Parent { get; init; }
}

public sealed record MeModel(
    Guid Id,
    string DisplayName,
    string TimeZone,
    bool Onboarded,
    bool TelegramLinked,
    DateTime? QuietUntilUtc,
    SettingsModel Settings,
    LimitsModel Limits,
    SleepModel? Sleep,
    IReadOnlyList<BlockModel> Blocks,
    IReadOnlyList<CategoryModel> Categories);

/// <summary>Частичное обновление профиля и настроек: что не задано — не меняется; <c>NormFactor</c>/<c>MaxFactor</c> = <c>null</c> — без ограничения.</summary>
public sealed record SettingsPatch
{
    public string? DisplayName { get; init; }

    public string? TimeZone { get; init; }

    public int? PingIntervalMin { get; init; }

    public Optional<double?> NormFactor { get; init; }

    public Optional<double?> MaxFactor { get; init; }

    public int? MaxUnansweredPings { get; init; }

    public bool? PingDuringBlocks { get; init; }

    public int? EveningReviewOffsetMin { get; init; }

    public int? RoundingMin { get; init; }
}

/// <summary>Блок из запроса клиента. <paramref name="Kind"/>: <c>fixed</c> или <c>custom</c>; <paramref name="Days"/> — дни ISO.</summary>
public sealed record BlockRequest(ScheduleBlockKind Kind, string? Title, string? Category, IReadOnlyList<int>? Days, TimeOnly Start, TimeOnly End);

public sealed record ActivityModel(
    Guid Id, string Name, string? Category, int? TypicalMin, DateTime? LastUsedUtc, IReadOnlyList<ActivityRuleModel> Rules);

/// <summary>Правило классификации занятия (spec §4.2): в эту часть суток (<c>morning</c>…<c>night</c>) — эта категория.</summary>
public sealed record ActivityRuleModel(string Part, string Category);

/// <summary>Правка занятия: <c>Category</c> и <c>TypicalMin</c> = <c>null</c> очищают значение.</summary>
public sealed record ActivityPatch
{
    public string? Name { get; init; }

    public Optional<string?> Category { get; init; }

    public Optional<int?> TypicalMin { get; init; }
}
