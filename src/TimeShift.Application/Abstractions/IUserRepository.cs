using TimeShift.Domain.Users;

namespace TimeShift.Application.Abstractions;

/// <summary>Хранилище пользователей (агрегат: настройки + шаблон недели + состояние пингов).</summary>
public interface IUserRepository
{
    Task<User?> FindByTelegramChatIdAsync(long chatId, CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Пользователь без Telegram (его заводит web-клиент, если больше никого нет).</summary>
    Task<User?> FindWithoutTelegramAsync(CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Любой пользователь (когда он единственный — владелец).</summary>
    Task<User?> FindFirstAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> ListOnboardedAsync(CancellationToken cancellationToken);

    void Add(User user);
}
