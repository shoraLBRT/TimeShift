namespace TimeShift.Telegram;

/// <summary>
/// Настройки адаптера Telegram (секция <c>Telegram</c>; в env — <c>Telegram__BotToken</c>, <c>Telegram__Proxy</c>,
/// <c>Telegram__AllowedChatIds__0</c>, <c>Telegram__AllowAnyone</c>).
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string? BotToken { get; init; }

    /// <summary>
    /// Прокси до api.telegram.org, например <c>socks5://127.0.0.1:1080</c> или <c>http://127.0.0.1:8080</c>
    /// (если VPN не системный). Пусто — напрямую.
    /// </summary>
    public string? Proxy { get; init; }

    /// <summary>
    /// Кому можно пользоваться ботом. Пусто и без <see cref="AllowAnyone"/> — никому: бот личный, посторонний
    /// чат получает отказ, а его chat id пишется в лог, чтобы владелец мог добавить себя.
    /// </summary>
    public IReadOnlyList<long> AllowedChatIds { get; init; } = [];

    /// <summary>Открытый режим: ботом может пользоваться любой личный чат, список не проверяется.</summary>
    public bool AllowAnyone { get; init; }

    /// <summary>Закрыт ли бот для всех (нет ни списка, ни открытого режима).</summary>
    public bool IsClosedToEveryone => !AllowAnyone && AllowedChatIds.Count == 0;

    public bool IsAllowed(long chatId) => AllowAnyone || AllowedChatIds.Contains(chatId);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(BotToken);
}
