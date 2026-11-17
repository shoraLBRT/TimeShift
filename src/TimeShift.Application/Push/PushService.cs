using System.Buffers.Text;
using System.Security.Cryptography;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Clients;
using TimeShift.Application.Conversations;
using TimeShift.Domain.Push;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Push;

/// <summary>Настройки VAPID из конфигурации (<c>Push:*</c>); ключи не заданы — сервер создаёт их сам и хранит в базе.</summary>
public sealed record PushOptions(string? PublicKey, string? PrivateKey, string Subject)
{
    public const string DefaultSubject = "mailto:owner@localhost";
}

/// <summary>Ключи VAPID: из конфигурации или созданные один раз и сохранённые в базе (деплой без настройки).</summary>
public sealed class VapidKeyProvider(PushOptions options, IServerKeyRepository serverKeys, IUnitOfWork unitOfWork)
{
    private const string PublicName = "vapid.public";

    private const string PrivateName = "vapid.private";

    private VapidKeys? _cached;

    public async Task<VapidKeys> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        if (!string.IsNullOrWhiteSpace(options.PublicKey) && !string.IsNullOrWhiteSpace(options.PrivateKey))
        {
            return _cached = new VapidKeys(options.PublicKey.Trim(), options.PrivateKey.Trim(), options.Subject);
        }

        var publicKey = await serverKeys.GetAsync(PublicName, cancellationToken);
        var privateKey = await serverKeys.GetAsync(PrivateName, cancellationToken);
        if (publicKey is null || privateKey is null)
        {
            (publicKey, privateKey) = Generate();
            serverKeys.Set(PublicName, publicKey);
            serverKeys.Set(PrivateName, privateKey);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return _cached = new VapidKeys(publicKey, privateKey, options.Subject);
    }

    /// <summary>Пара P-256: открытый — 65 байт (0x04 ‖ X ‖ Y), закрытый — 32 байта; оба base64url.</summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        byte[] publicPoint = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
        return (Base64Url.EncodeToString(publicPoint), Base64Url.EncodeToString(parameters.D!));
    }
}

public sealed record PushInfoModel(bool Enabled, string PublicKey, int Subscriptions);

/// <summary>Подписки и отправка Web Push для клиента и канала уведомлений.</summary>
public sealed class PushService(
    IPushSubscriptionRepository subscriptions,
    VapidKeyProvider vapid,
    IWebPushSender sender,
    IUnitOfWork unitOfWork,
    IClock clock,
    IFailureReporter failures)
{
    public const int MaxEndpointLength = 2048;

    private const int MaxKeyLength = 256;

    public async Task<PushInfoModel> InfoAsync(User user, CancellationToken cancellationToken)
    {
        var keys = await vapid.GetAsync(cancellationToken);
        return new PushInfoModel(true, keys.PublicKey, (await subscriptions.ListAsync(user.Id, cancellationToken)).Count);
    }

    public async Task<string> PublicKeyAsync(CancellationToken cancellationToken) => (await vapid.GetAsync(cancellationToken)).PublicKey;

    /// <summary>Подписка устройства; повтор с тем же адресом обновляет ключи (идемпотентно).</summary>
    public async Task SubscribeAsync(User user, string? endpoint, string? p256dh, string? auth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > MaxEndpointLength
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw ClientException.Invalid("endpoint", "Адрес подписки должен быть https-ссылкой.");
        }

        if (string.IsNullOrWhiteSpace(p256dh) || p256dh.Length > MaxKeyLength)
        {
            throw ClientException.Invalid("keys", "Не хватает ключа p256dh.");
        }

        if (string.IsNullOrWhiteSpace(auth) || auth.Length > MaxKeyLength)
        {
            throw ClientException.Invalid("keys", "Не хватает ключа auth.");
        }

        if (await subscriptions.FindByEndpointAsync(endpoint, cancellationToken) is { } existing)
        {
            existing.Renew(user.Id, p256dh, auth);
        }
        else
        {
            subscriptions.Add(new WebPushSubscription(user.Id, endpoint, p256dh, auth, clock.UtcNow));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Отписка; если такой подписки нет — тоже успех.</summary>
    public async Task UnsubscribeAsync(User user, string? endpoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw ClientException.Invalid("endpoint", "Нужен адрес подписки.");
        }

        if (await subscriptions.FindByEndpointAsync(endpoint, cancellationToken) is { } existing && existing.UserId == user.Id)
        {
            subscriptions.Remove(existing);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Пробное уведомление на все устройства; нет подписок — конфликт.</summary>
    public async Task TestAsync(User user, CancellationToken cancellationToken)
    {
        if ((await subscriptions.ListAsync(user.Id, cancellationToken)).Count == 0)
        {
            throw ClientException.Conflict("Нет подписанных устройств — включи уведомления на этом устройстве.");
        }

        await SendAsync(user, PushPayload.Test, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> HasSubscriptionsAsync(User user, CancellationToken cancellationToken) =>
        (await subscriptions.ListAsync(user.Id, cancellationToken)).Count > 0;

    /// <summary>
    /// Шлёт на все устройства пользователя (TTL — по виду уведомления, срочность высокая); ушедшие подписки (404/410)
    /// удаляет, успешные отмечает. Сбой одного устройства не мешает остальным. Сохраняет вызывающий.
    /// </summary>
    public async Task<int> SendAsync(User user, PushPayload payload, CancellationToken cancellationToken)
    {
        var list = await subscriptions.ListAsync(user.Id, cancellationToken);
        if (list.Count == 0)
        {
            return 0;
        }

        var keys = await vapid.GetAsync(cancellationToken);
        var json = payload.ToJson();
        var delivered = 0;
        foreach (var subscription in list)
        {
            try
            {
                var request = new PushRequest(subscription.Endpoint, subscription.P256dh, subscription.Auth, json, payload.TimeToLive, payload.Tag);
                if (await sender.SendAsync(request, keys, cancellationToken) == PushOutcome.Gone)
                {
                    subscriptions.Remove(subscription);
                }
                else
                {
                    subscription.MarkOk(clock.UtcNow);
                    delivered++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Report(e, $"Web push to subscription {subscription.Id} failed");
            }
        }

        return delivered;
    }
}

/// <summary>Канал уведомлений Web Push: пинг и вечерний разбор уходят на подписанные устройства; ссылки на сообщения нет.</summary>
public sealed class WebPushChannel(PushService push) : INotificationChannel
{
    public string Key => "wp";

    public Task<bool> CanReachAsync(User user, CancellationToken cancellationToken) => push.HasSubscriptionsAsync(user, cancellationToken);

    /// <summary>Новое уведомление с тем же <c>tag</c> заменяет прежнее, поэтому ссылка не нужна.</summary>
    public async Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken)
    {
        if (PushPayload.From(reply) is { } payload)
        {
            await push.SendAsync(user, payload, cancellationToken);
        }

        return null;
    }

    public Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken) => Task.CompletedTask;
}
