using TimeShift.Application.Conversations;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Abstractions;

/// <summary>Сообщения по инициативе бота (пинги). Реализует клиент — сейчас Telegram.</summary>
public interface INotifier
{
    /// <summary>Отправляет сообщение; возвращает ссылку на него (чтобы потом удалить) или <c>null</c>.</summary>
    Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken);

    /// <summary>Удаляет ранее отправленное сообщение; если его уже нет — молча.</summary>
    Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken);
}
