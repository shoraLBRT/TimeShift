using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TimeShift.Application.Conversations;

namespace TimeShift.Telegram;

/// <summary>
/// Long polling Telegram → <see cref="ConversationService"/> → ответы в чат. Только перевод форматов: логики бота
/// здесь нет. Обновления обрабатываются по одному, на каждое — свой scope (своя единица работы с БД).
/// </summary>
internal sealed partial class TelegramPollingService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopes,
    TelegramOptions options,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private static readonly BotCommand[] Commands =
    [
        new() { Command = "now", Description = "Что сейчас идёт, сменить занятие" },
        new() { Command = "day", Description = "Лента за сегодня (или /day вчера)" },
        new() { Command = "add", Description = "Записать прошедшее: вчера 19-21 Зал" },
        new() { Command = "quiet", Description = "Не беспокоить пару часов" },
        new() { Command = "undo", Description = "Отменить последнюю запись" },
        new() { Command = "blocks", Description = "Шаблон недели: сон, работа, свои блоки" },
        new() { Command = "settings", Description = "Пинги, продление, часовой пояс" },
        new() { Command = "help", Description = "Что я умею" },
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var me = await bot.GetMe(stoppingToken);
                LogStarted(me.Username);
                await bot.SetMyCommands(Commands, cancellationToken: stoppingToken);
                await bot.ReceiveAsync(
                    new DefaultUpdateHandler(HandleUpdateAsync, HandleErrorAsync),
                    new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LogConnectFailed(e, RetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        var (chat, from, incoming, callback) = Translate(update);
        if (chat is null || incoming is null || chat.Type != ChatType.Private)
        {
            return;
        }

        try
        {
            if (callback is not null)
            {
                await client.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
            }

            if (options.AllowedChatIds.Count > 0 && !options.AllowedChatIds.Contains(chat.Id))
            {
                LogRejected(chat.Id);
                await client.SendMessage(chat.Id, "Это личный бот, доступ закрыт.", cancellationToken: cancellationToken);
                return;
            }

            if (update.Message?.Voice is { } voice)
            {
                incoming = incoming with { Voice = await DownloadAsync(client, voice.FileId, cancellationToken) };
            }

            IReadOnlyList<BotReply> replies;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var conversation = scope.ServiceProvider.GetRequiredService<ConversationService>();
                replies = await conversation.HandleAsync(incoming, cancellationToken);
            }

            await SendAsync(client, chat.Id, callback?.Message, replies, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogHandleFailed(e, chat.Id, from?.Id);
            await client.SendMessage(chat.Id, "Что-то сломалось 😕 Попробуй ещё раз.", cancellationToken: cancellationToken);
        }
    }

    private static (Chat? Chat, User? From, IncomingMessage? Incoming, CallbackQuery? Callback) Translate(Update update)
    {
        switch (update)
        {
            case { Message: { Voice: { } voice } message }:
                return (message.Chat, message.From, new IncomingMessage(message.Chat.Id, Name(message.From, message.Chat))
                {
                    Voice = [], // скачивается в HandleUpdateAsync — здесь только признак
                    VoiceDuration = TimeSpan.FromSeconds(voice.Duration),
                }, null);

            case { Message: { } message }:
                return (message.Chat, message.From, new IncomingMessage(message.Chat.Id, Name(message.From, message.Chat), message.Text), null);

            case { CallbackQuery: { Message: { } message, Data: { } data } callback }:
                return (message.Chat, callback.From, IncomingMessage.FromButton(message.Chat.Id, Name(callback.From, message.Chat), data), callback);

            default:
                return (null, null, null, null);
        }
    }

    private static async Task<byte[]> DownloadAsync(ITelegramBotClient client, string fileId, CancellationToken cancellationToken)
    {
        var file = await client.GetFile(fileId, cancellationToken);
        using var buffer = new MemoryStream();
        await client.DownloadFile(file.FilePath!, buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static string Name(User? from, Chat chat) =>
        from?.FirstName ?? chat.FirstName ?? from?.Username ?? "друг";

    private static async Task SendAsync(
        ITelegramBotClient client, long chatId, Message? source, IReadOnlyList<BotReply> replies, CancellationToken cancellationToken)
    {
        var sourceHandled = false;
        foreach (var reply in replies)
        {
            var markup = TelegramMarkup.From(reply);
            if (reply.ReplacesSource && source is not null && !sourceHandled)
            {
                sourceHandled = true;
                try
                {
                    await client.EditMessageText(chatId, source.MessageId, reply.Text, replyMarkup: markup, cancellationToken: cancellationToken);
                }
                catch (ApiRequestException e) when (e.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
                {
                    // Нажали на уже выбранный вариант — сообщение не изменилось.
                }

                continue;
            }

            await client.SendMessage(chatId, reply.Text, replyMarkup: (ReplyMarkup?)markup, cancellationToken: cancellationToken);
        }

        // Ответили на кнопку новым сообщением — убираем кнопки у старого, чтобы не нажимали устаревшее.
        if (source is not null && !sourceHandled && source.ReplyMarkup is not null)
        {
            try
            {
                await client.EditMessageReplyMarkup(chatId, source.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
            }
            catch (ApiRequestException)
            {
                // Сообщение слишком старое или уже без кнопок — не важно.
            }
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        LogPollingError(exception, source);

        // Ошибка сети при опросе — небольшая пауза, чтобы не крутиться в цикле.
        return source == HandleErrorSource.PollingError
            ? Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)
            : Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Telegram bot @{Username} started (long polling)")]
    private partial void LogStarted(string? username);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cannot reach Telegram; retrying in {Seconds} s")]
    private partial void LogConnectFailed(Exception exception, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram polling error ({Source})")]
    private partial void LogPollingError(Exception exception, HandleErrorSource source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to handle update from chat {ChatId} (user {UserId})")]
    private partial void LogHandleFailed(Exception exception, long chatId, long? userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected chat {ChatId}: not in Telegram:AllowedChatIds")]
    private partial void LogRejected(long chatId);
}
