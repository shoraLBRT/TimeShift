namespace TimeShift.Telegram;

/// <summary>Настройки адаптера Telegram (секция <c>Telegram</c>, env <c>Telegram__BotToken</c>).</summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string? BotToken { get; init; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(BotToken);
}
