using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Microsoft.Extensions.Logging.Abstractions;
using TimeShift.Application.Abstractions;
using TimeShift.Speech;

namespace TimeShift.Infrastructure.Tests;

/// <summary>Голосовое OGG/Opus → PCM 16 кГц без FFmpeg; поведение без модели whisper.</summary>
public class SpeechTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Синус 440 Гц, моно 48 кГц, в OGG/Opus — как голосовое Telegram.</summary>
    private static byte[] OggTone(double seconds)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48_000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        using var output = new MemoryStream();
        var ogg = new OpusOggWriteStream(encoder, output);
        var samples = new short[(int)(48_000 * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 48_000) * 8000);
        }

        ogg.WriteSamples(samples, 0, samples.Length);
        ogg.Finish();
        return output.ToArray();
    }

    [Fact]
    public void Ogg_opus_is_decoded_to_16_khz_pcm()
    {
        using var stream = new MemoryStream(OggTone(2));

        var pcm = OggOpusDecoder.ToWhisperPcm(stream);

        Assert.InRange(pcm.Length, 30_000, 34_000);
        Assert.InRange(pcm.Max(), 0.1f, 1f);
        Assert.All(pcm, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public async Task Without_a_model_speech_is_disabled_and_fails_politely()
    {
        var options = new SpeechOptions { ModelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin") };
        using var speech = new WhisperSpeechToText(options, NullLogger<WhisperSpeechToText>.Instance);

        Assert.False(speech.IsEnabled);
        await Assert.ThrowsAsync<SpeechException>(() => speech.TranscribeAsync(new MemoryStream(OggTone(1)), Ct));
        Assert.Equal(string.Empty, await speech.TranscribeAsync(new MemoryStream([1, 2, 3, 4]), Ct));

        // Слишком короткое — тишина, до модели дело не доходит.
        Assert.Equal(string.Empty, await speech.TranscribeAsync(new MemoryStream(OggTone(0.1)), Ct));
    }
}
