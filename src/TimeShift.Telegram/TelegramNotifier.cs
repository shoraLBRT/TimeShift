using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Domain.Users;

namespace TimeShift.Telegram;

/// <summary>Сообщения по инициативе бота: ссылка на сообщение — его message_id.</summary>
internal sealed class TelegramNotifier(ITelegramBotClient bot) : INotificationChannel
{
    public string Key => "tg";

    public Task<bool> CanReachAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.TelegramChatId is not null);

    public async Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken)
    {
        // Пользователь без Telegram (только web-клиент) — слать некуда.
        if (user.TelegramChatId is not { } chatId)
        {
            return null;
        }

        if (reply.Image is { } image)
        {
            await TelegramMedia.SendPhotoAsync(bot, chatId, null, reply, image, TelegramMarkup.From(reply), cancellationToken);
            return null;
        }

        var message = await bot.SendMessage(
            chatId, reply.Text, replyMarkup: (ReplyMarkup?)TelegramMarkup.From(reply), cancellationToken: cancellationToken);
        return message.MessageId.ToString(CultureInfo.InvariantCulture);
    }

    public async Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken)
    {
        if (user.TelegramChatId is not { } chatId
            || !int.TryParse(messageRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var messageId))
        {
            return;
        }

        try
        {
            await bot.DeleteMessage(chatId, messageId, cancellationToken);
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
