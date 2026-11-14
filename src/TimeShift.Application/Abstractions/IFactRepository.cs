using TimeShift.Application.Llm;

namespace TimeShift.Application.Abstractions;

/// <summary>Факты пользователя в минутах (маршруты): ключ → минуты.</summary>
public interface IFactRepository
{
    Task<IReadOnlyDictionary<string, int>> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Добавляет или заменяет факт.</summary>
    Task SetAsync(Guid userId, string key, int minutes, CancellationToken cancellationToken);
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
