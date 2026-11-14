using TimeShift.Domain.Users;

namespace TimeShift.Application.Abstractions;

/// <summary>Хранилище пользователей (агрегат: настройки + шаблон недели + состояние пингов).</summary>
public interface IUserRepository
{
    Task<User?> FindByTelegramChatIdAsync(long chatId, CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> ListOnboardedAsync(CancellationToken cancellationToken);

    void Add(User user);
}
