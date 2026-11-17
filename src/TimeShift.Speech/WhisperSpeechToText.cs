using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TimeShift.Application.Abstractions;
using Whisper.net;
using Whisper.net.Ggml;

namespace TimeShift.Speech;

/// <summary>Настройки распознавания (секция <c>Speech</c>).</summary>
public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    /// <summary>Файл модели ggml (whisper.cpp).</summary>
    public string ModelPath { get; init; } = Path.Combine("models", "ggml-base.bin");

    /// <summary>Скачать модель с Hugging Face при первом голосовом, если файла нет.</summary>
    public bool AutoDownload { get; init; }

    /// <summary>Размер модели для скачивания: tiny, base, small, medium.</summary>
    public string Model { get; init; } = "base";

    public string Language { get; init; } = "ru";

    /// <summary>0 — по числу ядер.</summary>
    public int Threads { get; init; }

    public int MaxSeconds { get; init; } = 120;

    public static SpeechOptions From(IConfiguration configuration)
    {
        var s = configuration.GetSection(SectionName);
        var d = new SpeechOptions();
        return new SpeechOptions
        {
            ModelPath = string.IsNullOrWhiteSpace(s[nameof(ModelPath)]) ? d.ModelPath : s[nameof(ModelPath)]!,
            AutoDownload = bool.TryParse(s[nameof(AutoDownload)], out var a) && a,
            Model = string.IsNullOrWhiteSpace(s[nameof(Model)]) ? d.Model : s[nameof(Model)]!,
            Language = string.IsNullOrWhiteSpace(s[nameof(Language)]) ? d.Language : s[nameof(Language)]!,
            Threads = int.TryParse(s[nameof(Threads)], out var t) ? t : d.Threads,
            MaxSeconds = int.TryParse(s[nameof(MaxSeconds)], out var m) ? m : d.MaxSeconds,
        };
    }
}

/// <summary>
/// Локальный whisper.cpp через Whisper.net (spec §10.3): бесплатно, без внешних API. Модель грузится один раз при
/// первом голосовом; распознавания идут по одному (процессор не потокобезопасен, а VPS маленький).
/// </summary>
internal sealed partial class WhisperSpeechToText(SpeechOptions options, ILogger<WhisperSpeechToText> logger) : ISpeechToText, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public bool IsEnabled => options.AutoDownload || File.Exists(options.ModelPath);

    public TimeSpan MaxDuration => TimeSpan.FromSeconds(options.MaxSeconds);

    public async Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
    {
        float[] pcm;
        try
        {
            pcm = await DecodeAsync(audio, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new SpeechException("Не смог прочитать голосовое (ожидается OGG/Opus или WAV).", e);
        }

        if (pcm.Length < OggOpusDecoder.WhisperRate / 4)
        {
            return string.Empty;
        }

        // Длительность проверяет и вызывающий, но по заявленной; здесь — по тому, что реально пришло (секунда запаса).
        if (pcm.Length > (options.MaxSeconds + 1) * OggOpusDecoder.WhisperRate)
        {
            throw new SpeechException($"Запись длиннее {options.MaxSeconds} с.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var factory = await FactoryAsync(cancellationToken);
            var builder = factory.CreateBuilder().WithLanguage(options.Language);
            if (options.Threads > 0)
            {
                builder = builder.WithThreads(options.Threads);
            }

            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(pcm, cancellationToken))
            {
                text.Append(segment.Text);
            }

            return text.ToString().Trim();
        }
        catch (Exception e) when (e is not (OperationCanceledException or SpeechException))
        {
            throw new SpeechException("Распознавание не удалось.", e);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }

    /// <summary>Формат — по сигнатуре: «RIFF» — WAV (web-клиент), иначе OGG/Opus (Telegram).</summary>
    private static async Task<float[]> DecodeAsync(Stream audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken);
        if (WavDecoder.LooksLikeWav(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
        {
            return WavDecoder.ToWhisperPcm(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        buffer.Position = 0;
        return OggOpusDecoder.ToWhisperPcm(buffer);
    }

    private async Task<WhisperFactory> FactoryAsync(CancellationToken cancellationToken)
    {
        if (_factory is not null)
        {
            return _factory;
        }

        if (!File.Exists(options.ModelPath))
        {
            if (!options.AutoDownload)
            {
                throw new SpeechException($"Нет модели распознавания: {options.ModelPath}.");
            }

            if (!Enum.TryParse<GgmlType>(options.Model, ignoreCase: true, out var type))
            {
                throw new SpeechException($"Неизвестная модель Speech:Model «{options.Model}».");
            }

            LogDownloading(options.Model, options.ModelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ModelPath))!);
            var temporary = options.ModelPath + ".part";
            await using (var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, QuantizationType.NoQuantization, cancellationToken))
            await using (var file = File.Create(temporary))
            {
                await model.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporary, options.ModelPath, overwrite: true);
        }

        _factory = WhisperFactory.FromPath(options.ModelPath);
        LogLoaded(options.ModelPath);
        return _factory;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading whisper model {Model} to {Path}")]
    private partial void LogDownloading(string model, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Whisper model loaded from {Path}")]
    private partial void LogLoaded(string path);
}
