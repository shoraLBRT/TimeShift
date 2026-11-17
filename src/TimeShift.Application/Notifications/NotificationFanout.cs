using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Notifications;

/// <summary>
/// Единственный <see cref="INotifier"/>: шлёт во все каналы, сбой одного не мешает остальным (пишется в лог).
/// Ссылка на сообщение — <c>tg:123,wp:abc</c> (ключ канала и его ссылка), чтобы удаление нашло свой канал;
/// длина ссылки ограничена хранилищем (64). Ссылка без ключа — старый формат, это Telegram.
/// </summary>
public sealed class NotificationFanout(IEnumerable<INotificationChannel> channels, IFailureReporter failures) : INotifier
{
    public const int MaxRefLength = 64;

    private const string LegacyKey = "tg";

    private readonly List<INotificationChannel> _channels = channels.ToList();

    public async Task<bool> CanReachAsync(User user, CancellationToken cancellationToken)
    {
        foreach (var channel in _channels)
        {
            try
            {
                if (await channel.CanReachAsync(user, cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Report(e, $"Notification channel {channel.Key} failed");
            }
        }

        return false;
    }

    public async Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken)
    {
        var refs = new List<string>();
        foreach (var channel in _channels)
        {
            try
            {
                if (await channel.SendAsync(user, reply, cancellationToken) is { Length: > 0 } messageRef)
                {
                    refs.Add($"{channel.Key}:{messageRef}");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Report(e, $"Notification channel {channel.Key} failed");
            }
        }

        var joined = string.Join(',', refs);
        return joined.Length == 0 || joined.Length > MaxRefLength ? null : joined;
    }

    public async Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken)
    {
        foreach (var part in messageRef.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            var (key, value) = colon > 0 ? (part[..colon], part[(colon + 1)..]) : (LegacyKey, part);
            if (_channels.FirstOrDefault(c => c.Key == key) is not { } channel)
            {
                continue;
            }

            try
            {
                await channel.DeleteAsync(user, value, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Report(e, $"Notification channel {key} failed");
            }
        }
    }
}
