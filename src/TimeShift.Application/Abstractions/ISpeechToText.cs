namespace TimeShift.Application.Abstractions;

/// <summary>Не удалось распознать (нет модели, битый файл, ошибка движка).</summary>
public sealed class SpeechException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Распознавание голосовых (spec §10.3): запись → текст. Принимает OGG/Opus (голосовое Telegram) и WAV
/// (PCM; web-клиент перекодирует запись на устройстве) — формат узнаётся по сигнатуре. Сейчас — локальный
/// whisper.cpp; без модели <see cref="IsEnabled"/> = <c>false</c>, и клиент просит написать текстом.
/// </summary>
public interface ISpeechToText
{
    bool IsEnabled { get; }

    /// <summary>Самое длинное голосовое, которое берём в работу.</summary>
    TimeSpan MaxDuration { get; }

    /// <summary>Текст записи (пустая строка — тишина). Бросает <see cref="SpeechException"/>.</summary>
    Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken);
}
