using TimeShift.Application.Abstractions;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tracking;

/// <summary>Текущее открытое занятие и его карточка.</summary>
public sealed record CurrentActivity(Segment Segment, Activity? Activity)
{
    /// <summary>Сколько уже длится, включая время до перерывов.</summary>
    public TimeSpan Elapsed(DateTime nowUtc) => Segment.ElapsedBefore + (nowUtc - Segment.StartUtc);

    public OpenActivity ToOpenActivity() => OpenActivity.FromSegment(Segment, Activity?.TypicalDuration);
}

/// <summary>
/// Запись занятий в ленту: начать, продолжить, закончить, откатить (spec §5.3: ответ кнопкой — без LLM).
/// Изменения ленты пишутся в журнал для <c>/undo</c>. Сохранение — через <see cref="IUnitOfWork"/> вызывающего.
/// </summary>
public sealed class TrackingService(ITimelineRepository timelines, IActivityRepository activities, IFactRepository facts)
{
    /// <summary>Насколько назад загружать ленту для операций «сейчас».</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(2);

    /// <summary>Время отрезков — с точностью до минуты.</summary>
    public static DateTime Minute(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);

    public async Task<CurrentActivity?> CurrentAsync(User user, CancellationToken cancellationToken)
    {
        var open = await timelines.FindOpenAsync(user.Id, cancellationToken);
        if (open is null)
        {
            return null;
        }

        var activity = open.ActivityId is { } id ? await activities.GetAsync(user.Id, id, cancellationToken) : null;
        return new CurrentActivity(open, activity);
    }

    public Task<IReadOnlyList<Activity>> RecentAsync(User user, int count, CancellationToken cancellationToken) =>
        activities.RecentAsync(user.Id, count, cancellationToken);

    public Task<Activity?> GetActivityAsync(User user, Guid activityId, CancellationToken cancellationToken) =>
        activities.GetAsync(user.Id, activityId, cancellationToken);

    /// <summary>
    /// Удаляет свою категорию пользователя (и её подвиды) и снимает их с занятий — о категории такого занятия бот
    /// спросит заново; правила классификации, которые вели в неё, тоже пропадают. Отрезки ленты не меняются.
    /// Возвращает, сколько занятий осталось без категории.
    /// </summary>
    /// <exception cref="Domain.Categories.CategoryException">Категория системная или её нет.</exception>
    public async Task<int> RemoveCategoryAsync(User user, string key, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var removed = user.RemoveCategory(key, nowUtc);
        var affected = (await activities.ListAsync(user.Id, cancellationToken))
            .Where(a => a.CategoryKey is { } k && removed.Contains(k))
            .ToList();
        foreach (var activity in affected)
        {
            activity.CategoryKey = null;
        }

        foreach (var rule in (await facts.RulesAsync(user.Id, cancellationToken)).All.Where(r => removed.Contains(r.CategoryKey)).ToList())
        {
            await facts.RemoveRuleAsync(user.Id, rule.ActivityId, rule.Part, cancellationToken);
        }

        return affected.Count;
    }

    /// <summary>Правила классификации пользователя (spec §4.2) — факты «занятие в такую-то часть суток → категория».</summary>
    public Task<CategoryRules> RulesAsync(User user, CancellationToken cancellationToken) =>
        facts.RulesAsync(user.Id, cancellationToken);

    /// <summary>
    /// Категория нового отрезка занятия, начинающегося в <paramref name="startUtc"/>, когда она не названа явно:
    /// правило на эту часть суток, иначе категория занятия. Через это проходит каждая запись — диалог, намерения
    /// LLM, <c>/add</c>, клиенты; уже записанные отрезки правила не меняют.
    /// </summary>
    public async Task<string?> CategoryAsync(User user, Activity activity, DateTime startUtc, CancellationToken cancellationToken) =>
        (await facts.RulesAsync(user.Id, cancellationToken)).Resolve(activity, startUtc, user.TimeZoneInfo, user.Categories);

    /// <summary>
    /// Ответ о категории занятия доходит и до идущей записи (#62): открытый отрезок этого занятия, у которого
    /// категории ещё нет, получает ту, что дал бы ему сейчас <see cref="CategoryAsync"/> — правило на часть суток
    /// его начала, иначе категория занятия. Отрезок с категорией и уже закрытые записи не трогаются. Это правка
    /// ленты — отдельный шаг <c>/undo</c>: он снимает категорию с записи, а сам ответ (категория занятия или
    /// правило) остаётся. Возвращает исправленный отрезок или <c>null</c>, если менять нечего.
    /// Вызывать, пока в этой единице работы лента не менялась: открытый отрезок читается из хранилища.
    /// </summary>
    public async Task<Segment?> CategorizeCurrentAsync(User user, Activity activity, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var open = await timelines.FindOpenAsync(user.Id, cancellationToken);
        if (open is not { CategoryKey: null } || open.ActivityId != activity.Id
            || await CategoryAsync(user, activity, open.StartUtc, cancellationToken) is not { } category)
        {
            return null;
        }

        var replacement = open.WithCategory(category);
        return await EditAsync(user, open, replacement, nowUtc, cancellationToken) is null ? null : replacement;
    }

    /// <summary>Заводит правило или заменяет правило того же занятия на ту же часть суток.</summary>
    public Task SetRuleAsync(User user, Activity activity, PartOfDay part, string categoryKey, CancellationToken cancellationToken) =>
        facts.SetRuleAsync(user.Id, new CategoryRule(activity.Id, part, categoryKey), cancellationToken);

    /// <summary>Убирает правило. <c>false</c> — такого не было.</summary>
    public Task<bool> RemoveRuleAsync(User user, Guid activityId, PartOfDay part, CancellationToken cancellationToken) =>
        facts.RemoveRuleAsync(user.Id, activityId, part, cancellationToken);

    /// <summary>Находит занятие по названию или заводит новое (категория и длительность пока неизвестны).</summary>
    public async Task<Activity> ResolveActivityAsync(User user, string name, CancellationToken cancellationToken)
    {
        var existing = await activities.FindByNameAsync(user.Id, name, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new Activity(user.Id, Capitalize(name), Parsing.ScheduleText.GuessCategory(name));
        activities.Add(created);
        return created;
    }

    /// <summary>
    /// Начинает занятие с <paramref name="startUtc"/> (по умолчанию — сейчас); открытое до этого — закрывается.
    /// </summary>
    public async Task<Segment> StartAsync(
        User user, Activity activity, DateTime nowUtc, CancellationToken cancellationToken, DateTime? startUtc = null)
    {
        var now = Minute(startUtc is { } s && s < nowUtc ? s : nowUtc);
        var timeline = await timelines.LoadAsync(user.Id, now - RecentWindow, cancellationToken);
        var category = await CategoryAsync(user, activity, now, cancellationToken);
        var segment = new Segment(now, null, activity.Name, SegmentStatus.Confirmed, SegmentSource.User, activity.Id, category);
        var change = timeline.Apply([segment], now);
        timelines.Record(user.Id, change, nowUtc);
        return segment;
    }

    /// <summary>«Закончил» (в <paramref name="atUtc"/>, по умолчанию — сейчас). <c>null</c> — открытого не было.</summary>
    public async Task<Segment?> StopAsync(User user, DateTime nowUtc, CancellationToken cancellationToken, DateTime? atUtc = null)
    {
        var now = Minute(atUtc is { } a && a < nowUtc ? a : nowUtc);
        var timeline = await timelines.LoadAsync(user.Id, now - RecentWindow, cancellationToken);
        if (timeline.OpenSegment is null)
        {
            return null;
        }

        var change = timeline.Stop(now);
        timelines.Record(user.Id, change, nowUtc);
        return change.After.FirstOrDefault() ?? change.Before[0];
    }

    /// <summary>«Всё ещё занят». Возвращает продолженный открытый отрезок или <c>null</c>, если открытого не было.</summary>
    public async Task<Segment?> ContinueAsync(User user, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var now = Minute(nowUtc);
        var timeline = await timelines.LoadAsync(user.Id, now - RecentWindow, cancellationToken);
        if (timeline.OpenSegment is null)
        {
            return null;
        }

        var change = timeline.Continue(now);
        if (!change.IsEmpty)
        {
            timelines.Record(user.Id, change, nowUtc);
        }

        return timeline.OpenSegment;
    }

    /// <summary>
    /// Записывает готовые отрезки (разобранный период, параллельные занятия) как одно изменение журнала.
    /// Открытое занятие продолжается после них, если они кончились раньше «сейчас» (spec §3.6).
    /// </summary>
    public async Task<TimelineChange> ApplyAsync(User user, IReadOnlyList<Segment> segments, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return new TimelineChange([], []);
        }

        var from = segments.Min(s => s.StartUtc) - RecentWindow;
        var timeline = await timelines.LoadAsync(user.Id, from, cancellationToken);
        var change = timeline.Apply(segments, Minute(nowUtc));
        timelines.Record(user.Id, change, nowUtc);
        return change;
    }

    /// <summary>Заменяет отрезок исправленным (правка текстом) — одним изменением журнала.</summary>
    public async Task<TimelineChange?> EditAsync(User user, Segment existing, Segment replacement, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var from = (existing.StartUtc < replacement.StartUtc ? existing.StartUtc : replacement.StartUtc) - RecentWindow;
        var timeline = await timelines.LoadAsync(user.Id, from, cancellationToken);
        var stored = timeline.Segments.Concat(timeline.ParallelSegments).FirstOrDefault(s => s.Id == existing.Id);
        if (stored is null)
        {
            return null;
        }

        var change = timeline.Edit(stored, replacement, Minute(nowUtc));
        timelines.Record(user.Id, change, nowUtc);
        return change;
    }

    /// <summary>Убирает отрезок из ленты (время вернётся шаблону и фону) — одним изменением журнала. <c>null</c> — такого отрезка нет.</summary>
    public async Task<TimelineChange?> DeleteAsync(User user, Segment existing, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var timeline = await timelines.LoadAsync(user.Id, existing.StartUtc - RecentWindow, cancellationToken);
        var stored = timeline.Segments.Concat(timeline.ParallelSegments).FirstOrDefault(s => s.Id == existing.Id);
        if (stored is null)
        {
            return null;
        }

        var change = new TimelineChange([stored], []);
        timelines.Record(user.Id, change, nowUtc);
        return change;
    }

    /// <summary>Откатывает последнее изменение ленты. <c>false</c> — откатывать нечего или лента с тех пор поменялась.</summary>
    public async Task<UndoResult> UndoAsync(User user, CancellationToken cancellationToken)
    {
        var last = await timelines.LastChangeAsync(user.Id, cancellationToken);
        if (last is null)
        {
            return UndoResult.NothingToUndo;
        }

        var segments = last.Change.Before.Concat(last.Change.After).ToList();
        var from = segments.Count == 0 ? last.AtUtc : segments.Min(s => s.StartUtc);
        var timeline = await timelines.LoadAsync(user.Id, from.AddTicks(-1), cancellationToken);
        try
        {
            timeline.Undo(last.Change);
        }
        catch (InvalidOperationException)
        {
            return UndoResult.Stale;
        }

        await timelines.RevertAsync(user.Id, last, cancellationToken);
        return UndoResult.Undone;
    }

    /// <summary>Лента для окна: обе дорожки.</summary>
    public Task<Timeline> LoadAsync(User user, DateTime fromUtc, CancellationToken cancellationToken) =>
        timelines.LoadAsync(user.Id, fromUtc, cancellationToken);

    private static string Capitalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? trimmed : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}

public enum UndoResult
{
    Undone,
    NothingToUndo,
    Stale,
}
