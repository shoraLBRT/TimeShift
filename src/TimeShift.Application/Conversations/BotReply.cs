namespace TimeShift.Application.Conversations;

/// <summary>Кнопка под сообщением: подпись и данные, которые вернутся в <see cref="IncomingMessage.ButtonData"/>.</summary>
public sealed record ReplyButton(string Label, string Data);

/// <summary>
/// Ответ бота, не зависящий от клиента. <see cref="ReplacesSource"/> — заменить сообщение, на кнопку которого нажали
/// (меню настроек обновляется на месте), вместо отправки нового.
/// </summary>
public sealed record BotReply(string Text, IReadOnlyList<IReadOnlyList<ReplyButton>> Buttons, bool ReplacesSource = false)
{
    /// <summary>Картинка (PNG) — текст тогда подпись к ней (spec §5.7: /day, /week картинками).</summary>
    public byte[]? Image { get; init; }

    public static BotReply Plain(string text) => new(text, []);
}
