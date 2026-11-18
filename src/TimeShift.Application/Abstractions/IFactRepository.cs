using TimeShift.Application.Llm;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;

namespace TimeShift.Application.Abstractions;

/// <summary>Факты пользователя: длительности в минутах (маршруты) и правила классификации (spec §4.2).</summary>
public interface IFactRepository
{
    /// <summary>Факты-длительности: ключ → минуты.</summary>
    Task<IReadOnlyDictionary<string, int>> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Добавляет или заменяет факт.</summary>
    Task SetAsync(Guid userId, string key, int minutes, CancellationToken cancellationToken);

    /// <summary>Правила классификации пользователя, включая добавленные в этой единице работы.</summary>
    Task<CategoryRules> RulesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Добавляет правило или заменяет правило того же занятия на ту же часть суток.</summary>
    Task SetRuleAsync(Guid userId, CategoryRule rule, CancellationToken cancellationToken);

    /// <summary>Убирает правило. <c>false</c> — такого не было.</summary>
    Task<bool> RemoveRuleAsync(Guid userId, Guid activityId, PartOfDay part, CancellationToken cancellationToken);
}

/// <summary>Отложенный разбор периода: ждёт подтверждения или ответа на вопрос. Один на пользователя.</summary>
public interface IPendingPeriodRepository
{
    Task<PendingPeriod?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Заменяет отложенный разбор пользователя.</summary>
    Task SetAsync(PendingPeriod pending, CancellationToken cancellationToken);

    Task RemoveAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Журнал разборов LLM для отладки (spec §7: messages). Ключи сюда не попадают.</summary>
public interface IMessageLog
{
    void Add(MessageLogEntry entry);
}
