using TimeShift.Domain.Schedule;

namespace TimeShift.Domain.Users;

/// <summary>
/// Пользователь бота (spec §7: users) — агрегат вместе с настройками и шаблоном недели.
/// Инвариант: блоки шаблона не пересекаются.
/// </summary>
public class User
{
    private readonly List<ScheduleBlock> _blocks = [];

    public const string DefaultTimeZone = "Europe/Moscow";

    public User(long telegramChatId, string displayName, string timeZone, DateTime createdAtUtc)
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

    public long TelegramChatId { get; private set; }

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
