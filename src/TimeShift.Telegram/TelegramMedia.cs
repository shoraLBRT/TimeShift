using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TimeShift.Application.Conversations;

namespace TimeShift.Telegram;

/// <summary>Картинки ответов: фото с подписью; длинный текст — отдельным сообщением (лимит подписи 1024).</summary>
internal static class TelegramMedia
{
    private const int CaptionLimit = 1024;

    public static async Task SendPhotoAsync(
        ITelegramBotClient client, long chatId, Message? replace, BotReply reply, byte[] image, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        var fits = reply.Text.Length <= CaptionLimit;
        var caption = fits ? reply.Text : reply.Text.Split('\n')[0];
        using var stream = new MemoryStream(image, writable: false);
        if (replace is not null)
        {
            try
            {
                await client.EditMessageMedia(
                    chatId, replace.MessageId, new InputMediaPhoto(InputFile.FromStream(stream, "day.png")) { Caption = caption },
                    replyMarkup: fits ? markup : null, cancellationToken: ct);
                if (!fits)
                {
                    await client.SendMessage(chatId, reply.Text, replyMarkup: (ReplyMarkup?)markup, cancellationToken: ct);
                }

                return;
            }
            catch (ApiRequestException)
            {
                // Не вышло заменить (старое сообщение) — отправим новое.
                stream.Position = 0;
            }
        }

        await client.SendPhoto(chatId, InputFile.FromStream(stream, "day.png"), caption: caption, replyMarkup: fits ? markup : null, cancellationToken: ct);
        if (!fits)
        {
            await client.SendMessage(chatId, reply.Text, replyMarkup: (ReplyMarkup?)markup, cancellationToken: ct);
        }
    }
}
