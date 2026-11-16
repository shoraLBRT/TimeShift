using TimeShift.Application.Conversations;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>
/// Свободный текст и кнопки диалога из клиента — тот же <see cref="ConversationService"/>, что и в Telegram.
/// Онбординг в клиенте свой (экраны), поэтому до его конца диалог недоступен.
/// </summary>
public sealed class MessageService(ConversationService conversation)
{
    public const int MaxTextLength = 4000;

    /// <summary>Передаёт диалогу ровно одно из: <paramref name="text"/> или <paramref name="button"/>.</summary>
    public async Task<IReadOnlyList<BotReply>> HandleAsync(User user, string? text, string? button, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsOnboarded)
        {
            throw ClientException.Conflict("Сначала закончи настройку.");
        }

        IncomingMessage message;
        if (text is not null && button is null)
        {
            var trimmed = text.Trim();
            if (trimmed.Length is 0 or > MaxTextLength)
            {
                throw ClientException.Invalid("text", $"Текст — от 1 до {MaxTextLength} символов.");
            }

            message = IncomingMessage.FromText(0, user.DisplayName, trimmed);
        }
        else if (button is not null && text is null)
        {
            if (button.Length is 0 or > 200)
            {
                throw ClientException.Invalid("button", "Неверная кнопка.");
            }

            message = IncomingMessage.FromButton(0, user.DisplayName, button);
        }
        else
        {
            throw ClientException.Invalid("text", "Нужно одно из двух: text или button.");
        }

        return await conversation.HandleAsync(user, message, cancellationToken);
    }
}
