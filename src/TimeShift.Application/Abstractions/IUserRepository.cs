using TimeShift.Domain.Users;

namespace TimeShift.Application.Abstractions;

/// <summary>Хранилище пользователей (агрегат: настройки + шаблон недели). Одна единица работы на сообщение.</summary>
public interface IUserRepository
{
    Task<User?> FindByTelegramChatIdAsync(long chatId, CancellationToken cancellationToken);

    void Add(User user);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
