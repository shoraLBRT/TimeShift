namespace TimeShift.Application.Conversations;

/// <summary>Кнопка под сообщением: подпись и данные, которые вернутся в <see cref="IncomingMessage.ButtonData"/>.</summary>
public sealed record ReplyButton(string Label, string Data);

/// <summary>
/// Ответ бота, не зависящий от клиента. <see cref="ReplacesSource"/> — заменить сообщение, на кнопку которого нажали
/// (меню настроек обновляется на месте), вместо отправки нового.
/// </summary>
/// <summary>Что за уведомление (для каналов без кнопок и текста бота — Web Push).</summary>
public enum NoticeKind
{
    Ping,
    Evening,
}

/// <summary>
/// Клиент-нейтральная подсказка для уведомления: заголовок и короткий текст. <paramref name="CanAct"/> — можно ответить
/// «продолжаю» / «закончил» прямо из уведомления (идёт занятие).
/// </summary>
public sealed record ClientNotice(NoticeKind Kind, string Title, string Body, bool CanAct = false);

public sealed record BotReply(string Text, IReadOnlyList<IReadOnlyList<ReplyButton>> Buttons, bool ReplacesSource = false)
{
    /// <summary>Картинка (PNG) — текст тогда подпись к ней (spec §5.7: /day, /week картинками).</summary>
    public byte[]? Image { get; init; }

    /// <summary>Задано у пинга и вечернего разбора; остальные ответы не уведомления.</summary>
    public ClientNotice? Notice { get; init; }

    public static BotReply Plain(string text) => new(text, []);
}
