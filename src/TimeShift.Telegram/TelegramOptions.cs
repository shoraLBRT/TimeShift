namespace TimeShift.Telegram;

/// <summary>
/// Настройки адаптера Telegram (секция <c>Telegram</c>; в env — <c>Telegram__BotToken</c>, <c>Telegram__Proxy</c>,
/// <c>Telegram__AllowedChatIds__0</c>).
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

    /// <summary>Кому можно пользоваться ботом. Пусто — всем.</summary>
    public IReadOnlyList<long> AllowedChatIds { get; init; } = [];

    public bool IsEnabled => !string.IsNullOrWhiteSpace(BotToken);
}
