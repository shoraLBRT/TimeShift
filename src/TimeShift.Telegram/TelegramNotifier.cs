using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Domain.Users;

namespace TimeShift.Telegram;

/// <summary>Сообщения по инициативе бота: ссылка на сообщение — его message_id.</summary>
internal sealed class TelegramNotifier(ITelegramBotClient bot) : INotifier
{
    public async Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken)
    {
        var message = await bot.SendMessage(
            user.TelegramChatId, reply.Text, replyMarkup: (ReplyMarkup?)TelegramMarkup.From(reply), cancellationToken: cancellationToken);
        return message.MessageId.ToString(CultureInfo.InvariantCulture);
    }

    public async Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken)
    {
        if (!int.TryParse(messageRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var messageId))
        {
            return;
        }

        try
        {
            await bot.DeleteMessage(user.TelegramChatId, messageId, cancellationToken);
        }
        catch (ApiRequestException)
        {
            // Уже удалено пользователем или старше 48 часов — не важно.
        }
    }
}

internal static class TelegramMarkup
{
    public static InlineKeyboardMarkup? From(BotReply reply) =>
        reply.Buttons.Count == 0
            ? null
            : new InlineKeyboardMarkup(reply.Buttons.Select(row => row.Select(b => InlineKeyboardButton.WithCallbackData(b.Label, b.Data))));
}
