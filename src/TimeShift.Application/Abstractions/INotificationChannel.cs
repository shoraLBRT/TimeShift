using TimeShift.Application.Conversations;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Abstractions;

/// <summary>
/// Один способ достучаться до пользователя по инициативе бота (Telegram, Web Push). Ссылка на сообщение — своя у
/// каждого канала; объединяет каналы <c>NotificationFanout</c>.
/// </summary>
public interface INotificationChannel
{
    /// <summary>Короткий стабильный ключ канала (входит в ссылку на сообщение), например <c>tg</c>.</summary>
    string Key { get; }

    /// <summary>Есть ли у пользователя куда слать по этому каналу.</summary>
    Task<bool> CanReachAsync(User user, CancellationToken cancellationToken);

    /// <summary>Отправляет; возвращает ссылку на сообщение или <c>null</c>. Пользователь без этого канала — молча <c>null</c>.</summary>
    Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken);

    Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken);
}
