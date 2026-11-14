namespace TimeShift.Application.Abstractions;

/// <summary>Не удалось распознать (нет модели, битый файл, ошибка движка).</summary>
public sealed class SpeechException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Распознавание голосовых (spec §10.3): OGG/Opus из мессенджера → текст. Сейчас — локальный whisper.cpp;
/// без модели <see cref="IsEnabled"/> = <c>false</c>, и бот просит написать текстом.
/// </summary>
public interface ISpeechToText
{
    bool IsEnabled { get; }

    /// <summary>Самое длинное голосовое, которое берём в работу.</summary>
    TimeSpan MaxDuration { get; }

    /// <summary>Текст голосового (пустая строка — тишина). Бросает <see cref="SpeechException"/>.</summary>
    Task<string> TranscribeAsync(Stream oggOpus, CancellationToken cancellationToken);
}
