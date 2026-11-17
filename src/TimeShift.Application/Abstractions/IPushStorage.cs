using TimeShift.Domain.Push;

namespace TimeShift.Application.Abstractions;

/// <summary>Подписки устройств на Web Push.</summary>
public interface IPushSubscriptionRepository
{
    Task<IReadOnlyList<WebPushSubscription>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<WebPushSubscription?> FindByEndpointAsync(string endpoint, CancellationToken cancellationToken);

    void Add(WebPushSubscription subscription);

    void Remove(WebPushSubscription subscription);
}

/// <summary>Секреты, которые сервер создаёт сам (ключи VAPID): имя → значение.</summary>
public interface IServerKeyRepository
{
    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    void Set(string name, string value);
}

/// <summary>Ключи VAPID (base64url): <c>PublicKey</c> — 65-байтовая несжатая точка P-256, <c>PrivateKey</c> — 32 байта.</summary>
public sealed record VapidKeys(string PublicKey, string PrivateKey, string Subject);

/// <summary>Одно сообщение одному устройству.</summary>
public sealed record PushRequest(string Endpoint, string P256dh, string Auth, string Payload, TimeSpan TimeToLive, string? Topic);

public enum PushOutcome
{
    /// <summary>Push-сервис принял сообщение.</summary>
    Delivered,

    /// <summary>Подписки больше нет (404 / 410) — её нужно удалить.</summary>
    Gone,
}

/// <summary>Отправка по протоколу Web Push (VAPID, aes128gcm). Прочие ошибки — исключением.</summary>
public interface IWebPushSender
{
    Task<PushOutcome> SendAsync(PushRequest request, VapidKeys keys, CancellationToken cancellationToken);
}
