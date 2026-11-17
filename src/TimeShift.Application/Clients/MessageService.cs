using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>
/// Свободный текст, голос и кнопки диалога из клиента — тот же <see cref="ConversationService"/>, что и в Telegram.
/// Онбординг в клиенте свой (экраны), поэтому до его конца диалог недоступен.
/// </summary>
public sealed class MessageService(ConversationService conversation, ISpeechToText speech)
{
    public const int MaxTextLength = 4000;

    /// <summary>Заголовок WAV без дополнительных блоков.</summary>
    private const int WavHeaderBytes = 44;

    /// <summary>Запись клиента — WAV 16 кГц, моно, 16 бит: 32 000 байт на секунду.</summary>
    private const int WavBytesPerSecond = 16_000 * 2;

    /// <summary>Распознавание включено — клиент показывает микрофон.</summary>
    public bool VoiceEnabled => speech.IsEnabled;

    /// <summary>Самая длинная запись, которую берём в работу.</summary>
    public TimeSpan MaxVoiceDuration => speech.MaxDuration;

    /// <summary>Размер самой длинной записи; адаптеру незачем читать больше, чем на байт сверх этого.</summary>
    public int MaxVoiceBytes => WavHeaderBytes + checked((int)(speech.MaxDuration.TotalSeconds * WavBytesPerSecond));

    /// <summary>Передаёт диалогу ровно одно из: <paramref name="text"/> или <paramref name="button"/>.</summary>
    public async Task<IReadOnlyList<BotReply>> HandleAsync(User user, string? text, string? button, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        RequireOnboarded(user);

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

    /// <summary>
    /// Запись голоса из клиента (WAV 16 кГц, моно, 16 бит) — распознаётся и идёт в диалог как текст; первым ответом
    /// возвращается распознанное. Тишина и сбой распознавания — обычный ответ с причиной, не ошибка запроса.
    /// </summary>
    public async Task<IReadOnlyList<BotReply>> HandleVoiceAsync(User user, byte[] wav, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(wav);
        RequireOnboarded(user);
        if (!speech.IsEnabled)
        {
            throw ClientException.Conflict("Голос на сервере не включён — напиши текстом.");
        }

        if (wav.Length > MaxVoiceBytes)
        {
            throw ClientException.Invalid(
                "audio", $"Запись длиннее {TrackingReplies.Duration(speech.MaxDuration)} — раздели на части или напиши текстом.");
        }

        if (!IsWav(wav))
        {
            throw ClientException.Invalid("audio", "Ожидается запись WAV (16 кГц, моно, 16 бит).");
        }

        var duration = TimeSpan.FromSeconds((double)(wav.Length - WavHeaderBytes) / WavBytesPerSecond);
        return await conversation.HandleAsync(user, IncomingMessage.FromVoice(0, user.DisplayName, wav, duration), cancellationToken);
    }

    private static void RequireOnboarded(User user)
    {
        if (!user.IsOnboarded)
        {
            throw ClientException.Conflict("Сначала закончи настройку.");
        }
    }

    /// <summary>«RIFF····WAVE» и хоть что-то после заголовка.</summary>
    private static bool IsWav(byte[] audio) =>
        audio.Length > WavHeaderBytes && audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) && audio.AsSpan(8, 4).SequenceEqual("WAVE"u8);
}
