namespace TimeShift.Application.Conversations;

/// <summary>
/// Сообщение от клиента, уже переведённое адаптером: текст (включая команды вида <c>/blocks</c>) или нажатая
/// кнопка (<see cref="ButtonData"/>). Ровно одно из двух задано.
/// </summary>
public sealed record IncomingMessage(long ChatId, string DisplayName, string? Text = null, string? ButtonData = null)
{
    /// <summary>Голосовое (OGG/Opus или WAV) и его длительность — распознаётся в текст до разбора (spec §5.3).</summary>
    public byte[]? Voice { get; init; }

    public TimeSpan VoiceDuration { get; init; }

    public static IncomingMessage FromVoice(long chatId, string displayName, byte[] voice, TimeSpan duration) =>
        new(chatId, displayName) { Voice = voice, VoiceDuration = duration };

    public static IncomingMessage FromText(long chatId, string displayName, string text) => new(chatId, displayName, Text: text);

    public static IncomingMessage FromButton(long chatId, string displayName, string data) => new(chatId, displayName, ButtonData: data);
}
