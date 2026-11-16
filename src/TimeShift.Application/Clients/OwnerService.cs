using TimeShift.Application.Abstractions;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>
/// Владелец в режиме одного пользователя (web-клиент открывает данные одного человека по ключу доступа).
/// Кто он: пользователь Telegram с заданным чатом (создаётся, если ещё нет); иначе единственный в базе;
/// иначе пользователь без Telegram (создаётся, если ещё нет).
/// </summary>
public sealed class OwnerService(IUserRepository users, IUnitOfWork unitOfWork, IClock clock)
{
    public const string DefaultDisplayName = "Пользователь";

    public async Task<User> ResolveAsync(long? telegramChatId, CancellationToken cancellationToken)
    {
        User? owner;
        if (telegramChatId is { } chatId)
        {
            owner = await users.FindByTelegramChatIdAsync(chatId, cancellationToken);
            if (owner is not null)
            {
                return owner;
            }

            return await CreateAsync(chatId, cancellationToken);
        }

        if (await users.CountAsync(cancellationToken) == 1
            && await users.FindFirstAsync(cancellationToken) is { } only)
        {
            return only;
        }

        owner = await users.FindWithoutTelegramAsync(cancellationToken);
        return owner ?? await CreateAsync(null, cancellationToken);
    }

    private async Task<User> CreateAsync(long? chatId, CancellationToken cancellationToken)
    {
        var user = new User(chatId, DefaultDisplayName, User.DefaultTimeZone, clock.UtcNow);
        users.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return user;
    }
}
