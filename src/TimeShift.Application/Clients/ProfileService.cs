using TimeShift.Application.Abstractions;
using TimeShift.Application.Parsing;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>
/// Профиль, настройки и шаблон недели для клиента: те же правила, что в диалоге бота (/settings, /blocks, онбординг),
/// но по явным полям и с проверкой границ. Каждая операция сохраняет и возвращает обновлённый профиль.
/// </summary>
public sealed class ProfileService(IUnitOfWork unitOfWork, IClock clock, Tracking.TrackingService tracking)
{
    public const int MaxBlockTitleLength = 60;

    public MeModel Me(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var s = user.Settings;
        return new MeModel(
            user.Id,
            user.DisplayName,
            user.TimeZone,
            user.IsOnboarded,
            user.TelegramChatId is not null,
            user.QuietUntilUtc > clock.UtcNow ? user.QuietUntilUtc : null,
            new SettingsModel(s.PingIntervalMin, s.NormFactor, s.MaxFactor, s.MaxUnansweredPings, s.PingDuringBlocks, s.EveningReviewOffsetMin, s.RoundingMin),
            new LimitsModel(
                new IntRange(UserSettings.MinPingIntervalMin, UserSettings.MaxPingIntervalMin),
                new IntRange(UserSettings.MinUnansweredPingsLimit, UserSettings.MaxUnansweredPingsLimit),
                new IntRange(UserSettings.MinEveningReviewOffsetMin, UserSettings.MaxEveningReviewOffsetMin),
                UserSettings.RoundingOptionsMin),
            SleepOf(user),
            user.Blocks
                .Where(b => b.Kind != ScheduleBlockKind.Sleep)
                .OrderBy(b => b.Start)
                .ThenBy(b => b.Title, StringComparer.Ordinal)
                .Select(b => new BlockModel(b.Id, b.Kind, b.Title, b.CategoryKey, b.Days.Select(IsoDay).Order().ToList(), b.Start, b.End))
                .ToList(),
            CategoriesOf(user));
    }

    /// <summary>Весь набор пользователя: системные, свои и удалённые свои (их имена нужны старым записям).</summary>
    private static IReadOnlyList<CategoryModel> CategoriesOf(User user)
    {
        var set = user.Categories;
        return set.All
            .Select(c => new CategoryModel(c.Key, c.Name, c.Group, c.ParentKey, CategorySet.IsSystem(c.Key), set.IsArchived(c.Key)))
            .ToList();
    }

    // ---- Свои категории (spec §4.1) ---------------------------------------------------------------------------

    public async Task<MeModel> AddCategoryAsync(User user, CategoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parent is null && request.Group is null)
        {
            throw ClientException.Invalid("group", "Нужна группа или родительская категория.");
        }

        Checked(() => user.AddCategory(request.Name ?? string.Empty, request.Group ?? default, request.Parent, clock.UtcNow));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    public async Task<MeModel> UpdateCategoryAsync(User user, string key, CategoryPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var current = user.OwnCategories.FirstOrDefault(c => c.Key == key && !c.IsArchived);
        var parent = patch.Parent.IsSet ? patch.Parent.Value : current?.ParentKey;
        Checked(() => user.UpdateCategory(key, patch.Name ?? current?.Name ?? string.Empty, patch.Group ?? current?.Group ?? default, parent));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    /// <summary>Удаляет свою категорию: записи с ней остаются как были, занятия и блоки с ней — без категории.</summary>
    public async Task<MeModel> RemoveCategoryAsync(User user, string key, CancellationToken cancellationToken)
    {
        try
        {
            await tracking.RemoveCategoryAsync(user, key, clock.UtcNow, cancellationToken);
        }
        catch (CategoryException e)
        {
            throw Translate(e);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    private static void Checked(Action change)
    {
        try
        {
            change();
        }
        catch (CategoryException e)
        {
            throw Translate(e);
        }
    }

    /// <summary>Нет такой категории — 404, системная — 409, остальное — ошибка поля.</summary>
    private static ClientException Translate(CategoryException e) => e.Field != "key"
        ? ClientException.Invalid(e.Field, e.Message)
        : e.Message.StartsWith("Нет", StringComparison.Ordinal) ? ClientException.NotFound(e.Message) : ClientException.Conflict(e.Message);

    public async Task<MeModel> UpdateSettingsAsync(User user, SettingsPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var s = user.Settings;

        string? displayName = null;
        if (patch.DisplayName is { } rawName)
        {
            displayName = rawName.Trim();
            if (displayName.Length is 0 or > User.MaxDisplayNameLength)
            {
                throw ClientException.Invalid("displayName", $"Имя — от 1 до {User.MaxDisplayNameLength} символов.");
            }
        }

        string? timeZone = null;
        if (patch.TimeZone is { } rawZone)
        {
            timeZone = rawZone.Trim();
            if (!IsKnownZone(timeZone))
            {
                throw ClientException.Invalid("timeZone", $"Не знаю часовой пояс «{timeZone}». Нужно название вида Europe/Moscow.");
            }
        }

        if (patch.PingIntervalMin is { } ping && !UserSettings.IsValidPingInterval(ping))
        {
            throw ClientException.Invalid(
                "pingIntervalMin", $"Интервал — от {UserSettings.MinPingIntervalMin} до {UserSettings.MaxPingIntervalMin} минут.");
        }

        if (patch.MaxUnansweredPings is { } unanswered
            && unanswered is < UserSettings.MinUnansweredPingsLimit or > UserSettings.MaxUnansweredPingsLimit)
        {
            throw ClientException.Invalid(
                "maxUnansweredPings", $"Неотвеченных пингов до паузы — от {UserSettings.MinUnansweredPingsLimit} до {UserSettings.MaxUnansweredPingsLimit}.");
        }

        if (patch.EveningReviewOffsetMin is { } offset
            && offset is < UserSettings.MinEveningReviewOffsetMin or > UserSettings.MaxEveningReviewOffsetMin)
        {
            throw ClientException.Invalid(
                "eveningReviewOffsetMin", $"Вечерний разбор — за {UserSettings.MinEveningReviewOffsetMin}–{UserSettings.MaxEveningReviewOffsetMin} минут до сна.");
        }

        if (patch.RoundingMin is { } rounding && !UserSettings.RoundingOptionsMin.Contains(rounding))
        {
            throw ClientException.Invalid("roundingMin", $"Округление — одно из: {string.Join(", ", UserSettings.RoundingOptionsMin)} минут.");
        }

        var norm = patch.NormFactor.IsSet ? patch.NormFactor.Value : s.NormFactor;
        var max = patch.MaxFactor.IsSet ? patch.MaxFactor.Value : s.MaxFactor;
        if (patch.NormFactor.IsSet || patch.MaxFactor.IsSet)
        {
            ValidateFactors(norm, max);
        }

        if (displayName is not null)
        {
            user.DisplayName = displayName;
        }

        if (timeZone is not null)
        {
            user.TimeZone = timeZone;
        }

        s.PingIntervalMin = patch.PingIntervalMin ?? s.PingIntervalMin;
        s.NormFactor = norm;
        s.MaxFactor = max;
        s.MaxUnansweredPings = patch.MaxUnansweredPings ?? s.MaxUnansweredPings;
        s.PingDuringBlocks = patch.PingDuringBlocks ?? s.PingDuringBlocks;
        s.EveningReviewOffsetMin = patch.EveningReviewOffsetMin ?? s.EveningReviewOffsetMin;
        s.RoundingMin = patch.RoundingMin ?? s.RoundingMin;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    /// <summary>Сон: каждую ночь одинаково или отдельно перед выходными. Пересечение с блоком — конфликт.</summary>
    public async Task<MeModel> SetSleepAsync(User user, SleepModel sleep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sleep);
        if (sleep.Start == sleep.End)
        {
            throw ClientException.Invalid("end", "Отбой и подъём не могут совпадать.");
        }

        if (sleep.Weekend is { } w && w.Start == w.End)
        {
            throw ClientException.Invalid("weekend", "Отбой и подъём в выходные не могут совпадать.");
        }

        var conflict = user.TrySetSleep((sleep.Start, sleep.End), sleep.Weekend is { } we ? (we.Start, we.End) : null);
        if (conflict is not null)
        {
            throw ClientException.Conflict($"Сон пересекается с блоком «{conflict.Title}».");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    public async Task<MeModel> AddBlockAsync(User user, BlockRequest request, CancellationToken cancellationToken)
    {
        var block = Build(user, request, null);
        if (user.TryAddBlock(block) is { } conflict)
        {
            throw ClientException.Conflict($"Пересекается с блоком «{conflict.Title}».");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    /// <summary>Заменяет блок: убирает старый и добавляет новый; при конфликте старый возвращается.</summary>
    public async Task<MeModel> UpdateBlockAsync(User user, Guid blockId, BlockRequest request, CancellationToken cancellationToken)
    {
        var old = user.Blocks.FirstOrDefault(b => b.Id == blockId && b.Kind != ScheduleBlockKind.Sleep)
            ?? throw ClientException.NotFound("Такого блока нет.");
        var replacement = Build(user, request, old);
        user.RemoveBlock(blockId);
        if (user.TryAddBlock(replacement) is { } conflict)
        {
            user.TryAddBlock(old);
            throw ClientException.Conflict($"Пересекается с блоком «{conflict.Title}».");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    public async Task<MeModel> RemoveBlockAsync(User user, Guid blockId, CancellationToken cancellationToken)
    {
        if (user.Blocks.All(b => b.Id != blockId || b.Kind == ScheduleBlockKind.Sleep) || !user.RemoveBlock(blockId))
        {
            throw ClientException.NotFound("Такого блока нет.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Me(user);
    }

    /// <summary>Заканчивает онбординг; нужен хотя бы сон. Время до этого момента разбирать не нужно.</summary>
    public async Task<MeModel> CompleteOnboardingAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.IsOnboarded)
        {
            if (user.Blocks.All(b => b.Kind != ScheduleBlockKind.Sleep))
            {
                throw ClientException.Conflict("Сначала укажи, когда ты спишь.");
            }

            user.DialogState = null;
            user.CompleteOnboarding();
            user.MarkYellowReviewed(clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Me(user);
    }

    private static ScheduleBlock Build(User user, BlockRequest request, ScheduleBlock? replaced)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (ScheduleBlockKind.Fixed or ScheduleBlockKind.Custom))
        {
            throw ClientException.Invalid("kind", "Тип блока — fixed или custom; сон задаётся отдельно.");
        }

        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length is 0 or > MaxBlockTitleLength)
        {
            throw ClientException.Invalid("title", $"Название — от 1 до {MaxBlockTitleLength} символов.");
        }

        string? category;
        if (request.Category is { } key)
        {
            category = user.Categories.FindActive(key) is not null ? key : throw ClientException.Invalid("category", "Нет такой категории.");
        }
        else
        {
            category = ScheduleText.GuessCategory(title);
        }

        var days = request.Days;
        if (days is not { Count: > 0 } || days.Any(d => d is < 1 or > 7))
        {
            throw ClientException.Invalid("days", "Дни — числа от 1 (понедельник) до 7 (воскресенье), хотя бы один.");
        }

        if (request.Start == request.End)
        {
            throw ClientException.Invalid("end", "Начало и конец блока не могут совпадать.");
        }

        return new ScheduleBlock(
            request.Kind, title, category, days.Select(FromIsoDay), request.Start, request.End, replaced?.ActivityId, replaced?.Id);
    }

    private static void ValidateFactors(double? norm, double? max)
    {
        if (norm is { } n && (!double.IsFinite(n) || n <= 0))
        {
            throw ClientException.Invalid("normFactor", "Норма продления должна быть больше нуля (или без ограничения).");
        }

        if (max is { } m && (!double.IsFinite(m) || m <= 0))
        {
            throw ClientException.Invalid("maxFactor", "Предел продления должен быть больше нуля (или без ограничения).");
        }

        var normValue = norm ?? double.PositiveInfinity;
        var maxValue = max ?? double.PositiveInfinity;
        if (maxValue < normValue)
        {
            throw ClientException.Invalid("maxFactor", "Предел продления не может быть меньше нормы.");
        }
    }

    private static bool IsKnownZone(string id)
    {
        if (id.Length is 0 or > 64)
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return false;
        }
    }

    private static SleepModel? SleepOf(User user)
    {
        var sleeps = user.Blocks.Where(b => b.Kind == ScheduleBlockKind.Sleep).ToList();
        if (sleeps.Count == 0)
        {
            return null;
        }

        if (sleeps.Count == 1)
        {
            return new SleepModel(sleeps[0].Start, sleeps[0].End, null);
        }

        bool Matches(ScheduleBlock b, bool beforeWeekend) =>
            b.Days.SetEquals(ScheduleBlock.SleepDays(b.Start, beforeWeekend));
        var weekday = sleeps.FirstOrDefault(b => Matches(b, false)) ?? sleeps[0];
        var weekend = sleeps.FirstOrDefault(b => Matches(b, true) && b != weekday) ?? sleeps.First(b => b != weekday);
        return new SleepModel(weekday.Start, weekday.End, new SleepRange(weekend.Start, weekend.End));
    }

    private static int IsoDay(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private static DayOfWeek FromIsoDay(int day) => day == 7 ? DayOfWeek.Sunday : (DayOfWeek)day;
}
