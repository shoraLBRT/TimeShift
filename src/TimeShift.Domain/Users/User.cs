using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;

namespace TimeShift.Domain.Users;

/// <summary>
/// Пользователь бота (spec §7: users) — агрегат вместе с настройками и шаблоном недели.
/// Инвариант: блоки шаблона не пересекаются.
/// </summary>
public class User
{
    private readonly List<ScheduleBlock> _blocks = [];
    private readonly List<UserCategory> _categories = [];

    public const string DefaultTimeZone = "Europe/Moscow";

    public const int MaxDisplayNameLength = 60;

    /// <summary>Сколько своих (не удалённых) категорий может быть у пользователя.</summary>
    public const int MaxOwnCategories = 50;

    public User(long? telegramChatId, string displayName, string timeZone, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        if (createdAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Время хранится только в UTC.", nameof(createdAtUtc));
        }

        TelegramChatId = telegramChatId;
        DisplayName = displayName;
        TimeZone = timeZone;
        CreatedAtUtc = createdAtUtc;
        Settings = new UserSettings();
    }

    // Для EF Core.
    private User()
    {
        DisplayName = string.Empty;
        TimeZone = DefaultTimeZone;
        Settings = null!;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Чат в Telegram; <c>null</c> — пользователь без Telegram (работает только через web-клиент).</summary>
    public long? TelegramChatId { get; private set; }

    public string DisplayName { get; set; }

    /// <summary>IANA-идентификатор часового пояса.</summary>
    public string TimeZone { get; set; }

    public DateTime CreatedAtUtc { get; private set; }

    public UserSettings Settings { get; private set; }

    /// <summary>Онбординг пройден (spec §5.1).</summary>
    public bool IsOnboarded { get; private set; }

    /// <summary>
    /// Состояние диалога с клиентом (что бот ждёт в ответ). Непрозрачная строка: её формат принадлежит
    /// слою Application; <c>null</c> — ничего не ждёт.
    /// </summary>
    public string? DialogState { get; set; }

    /// <summary>Когда пользователь последний раз писал или нажимал кнопку.</summary>
    public DateTime? LastSeenAtUtc { get; private set; }

    /// <summary>Когда ушёл последний пинг (spec §5.2).</summary>
    public DateTime? LastPingAtUtc { get; private set; }

    /// <summary>Ссылка клиента на сообщение последнего пинга — чтобы удалить его, если не ответили.</summary>
    public string? LastPingMessageRef { get; private set; }

    /// <summary>Сколько пингов подряд остались без ответа; на <c>MaxUnansweredPings</c> пинги встают на паузу.</summary>
    public int UnansweredPings { get; private set; }

    /// <summary>Ручные тихие часы (spec §5.7: /quiet): не пинговать до этого момента.</summary>
    public DateTime? QuietUntilUtc { get; private set; }

    /// <summary>До какого момента участки 🟡 уже предлагались к разбору (spec §5.4) — чтобы не спрашивать дважды.</summary>
    public DateTime? YellowReviewedUntilUtc { get; private set; }

    /// <summary>Местная дата последнего вечернего разбора (spec §5.4) — раз в день.</summary>
    public DateOnly? LastEveningReview { get; private set; }

    /// <summary>Местная дата последнего утреннего вопроса про сон.</summary>
    public DateOnly? LastMorningCheck { get; private set; }

    public void MarkEveningReview(DateOnly localDate) => LastEveningReview = localDate;

    public void MarkMorningCheck(DateOnly localDate) => LastMorningCheck = localDate;

    public void MarkYellowReviewed(DateTime untilUtc)
    {
        Utc.Ensure(untilUtc, nameof(untilUtc));
        if (YellowReviewedUntilUtc is null || untilUtc > YellowReviewedUntilUtc)
        {
            YellowReviewedUntilUtc = untilUtc;
        }
    }

    /// <summary>Пользователь что-то написал: пинги считаются отвеченными, пауза снимается.</summary>
    public void RegisterActivity(DateTime atUtc)
    {
        Utc.Ensure(atUtc, nameof(atUtc));
        LastSeenAtUtc = atUtc;
        UnansweredPings = 0;
    }

    /// <summary>Пинг отправлен. Возвращает ссылку на предыдущий неотвеченный пинг — его нужно удалить.</summary>
    public string? RegisterPing(DateTime atUtc, string? messageRef)
    {
        Utc.Ensure(atUtc, nameof(atUtc));
        var previous = UnansweredPings > 0 ? LastPingMessageRef : null;
        LastPingAtUtc = atUtc;
        LastPingMessageRef = messageRef;
        UnansweredPings++;
        return previous;
    }

    public void SetQuietUntil(DateTime? untilUtc)
    {
        if (untilUtc is { } until)
        {
            Utc.Ensure(until, nameof(untilUtc));
        }

        QuietUntilUtc = untilUtc;
    }

    /// <summary>Блоки шаблона недели.</summary>
    public IReadOnlyList<ScheduleBlock> Blocks => _blocks;

    public TimeZoneInfo TimeZoneInfo => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public WeekTemplate Template() => new(_blocks, TimeZoneInfo);

    public void CompleteOnboarding() => IsOnboarded = true;

    /// <summary>Добавляет блок; если он пересекается с существующим — не добавляет и возвращает тот блок.</summary>
    public ScheduleBlock? TryAddBlock(ScheduleBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var conflict = Conflict([.. _blocks, block], block);
        if (conflict is null)
        {
            _blocks.Add(block);
        }

        return conflict;
    }

    public bool RemoveBlock(Guid blockId) => _blocks.RemoveAll(b => b.Id == blockId) > 0;

    /// <summary>
    /// Заменяет блоки «Сон». Без <paramref name="weekend"/> — одинаковый сон каждую ночь; с ним — отдельно ночи
    /// перед буднями и перед выходными (см. <see cref="ScheduleBlock.SleepDays"/>). Если новый сон пересекается с
    /// другим блоком, ничего не меняет и возвращает этот блок.
    /// </summary>
    public ScheduleBlock? TrySetSleep((TimeOnly Start, TimeOnly End) everyNight, (TimeOnly Start, TimeOnly End)? weekend = null)
    {
        List<ScheduleBlock> sleep = weekend is { } w
            ?
            [
                ScheduleBlock.Sleep(ScheduleBlock.SleepDays(everyNight.Start, beforeWeekend: false), everyNight.Start, everyNight.End),
                ScheduleBlock.Sleep(ScheduleBlock.SleepDays(w.Start, beforeWeekend: true), w.Start, w.End),
            ]
            : [ScheduleBlock.Sleep(DaysOfWeek.All, everyNight.Start, everyNight.End)];

        var others = _blocks.Where(b => b.Kind != ScheduleBlockKind.Sleep).ToList();
        foreach (var block in sleep)
        {
            if (Conflict([.. others, .. sleep], block) is { } conflict)
            {
                return conflict;
            }
        }

        _blocks.RemoveAll(b => b.Kind == ScheduleBlockKind.Sleep);
        _blocks.InsertRange(0, sleep);
        return null;
    }

    // ---- Категории (spec §4.1) ---------------------------------------------------------------------------------

    /// <summary>Свои категории пользователя, включая удалённые (архивные).</summary>
    public IReadOnlyList<UserCategory> OwnCategories => _categories;

    /// <summary>Набор категорий пользователя: системные и свои.</summary>
    public CategorySet Categories => new(_categories);

    /// <summary>
    /// Заводит свою категорию. С <paramref name="parentKey"/> — подвид категории верхнего уровня (группа берётся у
    /// родителя). Если категория с таким именем была удалена — возвращает её с прежним ключом, и старые отрезки
    /// снова считаются вместе с новыми.
    /// </summary>
    /// <exception cref="CategoryException">Имя пустое, длинное или занято; нет такого родителя; слишком много категорий.</exception>
    public UserCategory AddCategory(string name, CategoryGroup group, string? parentKey, DateTime nowUtc)
    {
        Utc.Ensure(nowUtc, nameof(nowUtc));
        var (cleanName, effectiveGroup) = CheckCategory(name, group, parentKey, self: null);
        if (_categories.Count(c => !c.IsArchived) >= MaxOwnCategories)
        {
            throw new CategoryException("name", $"Своих категорий может быть не больше {MaxOwnCategories}.");
        }

        var wanted = CategorySet.Normalize(cleanName);
        var archived = _categories.FirstOrDefault(c => c.IsArchived && CategorySet.Normalize(c.Name) == wanted
            && (parentKey is null || _categories.All(child => child.ParentKey != c.Key)));
        if (archived is not null)
        {
            archived.ArchivedAtUtc = null;
            archived.Name = cleanName;
            Place(archived, effectiveGroup, parentKey);
            return archived;
        }

        var category = new UserCategory(cleanName, effectiveGroup, parentKey, nowUtc);
        _categories.Add(category);
        return category;
    }

    /// <summary>Меняет имя, группу и родителя своей категории. Ключ остаётся прежним, отрезки не трогаются.</summary>
    /// <exception cref="CategoryException">Категория системная или её нет; имя или родитель не подходят.</exception>
    public UserCategory UpdateCategory(string key, string name, CategoryGroup group, string? parentKey)
    {
        var category = OwnActive(key);
        var (cleanName, effectiveGroup) = CheckCategory(name, group, parentKey, category);
        category.Name = cleanName;
        Place(category, effectiveGroup, parentKey);
        return category;
    }

    /// <summary>
    /// Удаляет свою категорию вместе с её подвидами: они уходят в архив и пропадают из выбора, блоки шаблона с ними
    /// остаются без категории. Отрезки ленты не меняются — имя и родитель категории по-прежнему известны.
    /// Возвращает ключи удалённых категорий (чтобы снять их с занятий).
    /// </summary>
    /// <exception cref="CategoryException">Категория системная или её нет.</exception>
    public IReadOnlyList<string> RemoveCategory(string key, DateTime nowUtc)
    {
        Utc.Ensure(nowUtc, nameof(nowUtc));
        var category = OwnActive(key);
        var removed = _categories.Where(c => !c.IsArchived && (c == category || c.ParentKey == category.Key)).ToList();
        foreach (var c in removed)
        {
            c.ArchivedAtUtc = nowUtc;
        }

        var keys = removed.Select(c => c.Key).ToList();
        foreach (var block in _blocks.Where(b => b.CategoryKey is { } k && keys.Contains(k)))
        {
            block.ClearCategory();
        }

        return keys;
    }

    private UserCategory OwnActive(string key)
    {
        if (CategorySet.IsSystem(key))
        {
            throw new CategoryException(CategoryException.SystemKey, "Системную категорию нельзя изменить или удалить.");
        }

        return _categories.FirstOrDefault(c => c.Key == key && !c.IsArchived)
            ?? throw new CategoryException(CategoryException.UnknownKey, "Нет такой категории.");
    }

    /// <summary>Проверяет имя, группу и родителя; возвращает очищенное имя и итоговую группу (у подвида — группа родителя).</summary>
    private (string Name, CategoryGroup Group) CheckCategory(string name, CategoryGroup group, string? parentKey, UserCategory? self)
    {
        var cleanName = name?.Trim() ?? string.Empty;
        if (cleanName.Length is 0 or > UserCategory.MaxNameLength)
        {
            throw new CategoryException("name", $"Название — от 1 до {UserCategory.MaxNameLength} символов.");
        }

        var set = Categories;
        if (set.FindByName(cleanName) is { } same && same.Key != self?.Key)
        {
            throw new CategoryException("name", $"Категория «{same.Name}» уже есть.");
        }

        if (parentKey is null)
        {
            return Enum.IsDefined(group)
                ? (cleanName, group)
                : throw new CategoryException("group", "Нет такой группы категорий.");
        }

        if (set.FindActive(parentKey) is not { ParentKey: null } parent || parent.Key == self?.Key)
        {
            throw new CategoryException("parent", "Подвид можно завести только у категории верхнего уровня.");
        }

        if (self is not null && _categories.Any(c => !c.IsArchived && c.ParentKey == self.Key))
        {
            throw new CategoryException("parent", "У этой категории есть свои подвиды — сначала убери или перенеси их.");
        }

        return (cleanName, parent.Group);
    }

    /// <summary>Ставит категорию в группу или под родителя; её подвиды (в том числе удалённые) идут следом.</summary>
    private void Place(UserCategory category, CategoryGroup group, string? parentKey)
    {
        category.Group = group;
        category.ParentKey = parentKey;
        foreach (var child in _categories.Where(c => c.ParentKey == category.Key))
        {
            child.Group = group;

            // Уровней два: если категория сама стала подвидом, её удалённые подвиды переходят к новому родителю.
            if (parentKey is not null)
            {
                child.ParentKey = parentKey;
            }
        }
    }

    private static ScheduleBlock? Conflict(List<ScheduleBlock> blocks, ScheduleBlock candidate)
    {
        if (WeekTemplate.FindOverlap(blocks) is not var (first, second))
        {
            return null;
        }

        // Возвращаем «другой» блок пары, если кандидат в ней участвует.
        return ReferenceEquals(first, candidate) ? second : first;
    }
}
