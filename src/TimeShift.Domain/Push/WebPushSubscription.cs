namespace TimeShift.Domain.Push;

/// <summary>Подписка устройства на Web Push (push_subscriptions). Одно устройство — один <see cref="Endpoint"/>.</summary>
public sealed class WebPushSubscription
{
    public WebPushSubscription(Guid userId, string endpoint, string p256dh, string auth, DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(p256dh);
        ArgumentException.ThrowIfNullOrWhiteSpace(auth);
        Utc.Ensure(createdAtUtc, nameof(createdAtUtc));
        UserId = userId;
        Endpoint = endpoint;
        P256dh = p256dh;
        Auth = auth;
        CreatedAtUtc = createdAtUtc;
    }

    // Для EF Core.
    private WebPushSubscription()
    {
        Endpoint = string.Empty;
        P256dh = string.Empty;
        Auth = string.Empty;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    /// <summary>Адрес push-сервиса браузера; уникален.</summary>
    public string Endpoint { get; private set; }

    public string P256dh { get; private set; }

    public string Auth { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Когда push-сервис последний раз принял сообщение.</summary>
    public DateTime? LastOkAtUtc { get; private set; }

    /// <summary>Повторная подписка того же устройства: ключи и владелец могли смениться.</summary>
    public void Renew(Guid userId, string p256dh, string auth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(p256dh);
        ArgumentException.ThrowIfNullOrWhiteSpace(auth);
        UserId = userId;
        P256dh = p256dh;
        Auth = auth;
    }

    public void MarkOk(DateTime atUtc)
    {
        Utc.Ensure(atUtc, nameof(atUtc));
        LastOkAtUtc = atUtc;
    }
}
