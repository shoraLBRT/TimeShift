using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Microsoft.Extensions.Logging.Abstractions;
using TimeShift.Application.Abstractions;
using TimeShift.Speech;

namespace TimeShift.Infrastructure.Tests;

/// <summary>Голосовое OGG/Opus и WAV → PCM 16 кГц без FFmpeg; поведение без модели whisper.</summary>
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

    /// <summary>Синус 440 Гц в WAV: PCM 16 бит или float 32 бит, каналы одинаковые.</summary>
    private static byte[] WavTone(double seconds, int rate = 16_000, int channels = 1, bool isFloat = false, bool extensible = false)
    {
        var bytes = isFloat ? 4 : 2;
        var frames = (int)(rate * seconds);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var fmtSize = extensible ? 40 : 16;
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);

        // Посторонний блок нечётной длины перед fmt — разбор должен его пропустить вместе с байтом выравнивания.
        writer.Write("LIST"u8);
        writer.Write(3);
        writer.Write(new byte[4]);
        writer.Write("fmt "u8);
        writer.Write(fmtSize);
        writer.Write(extensible ? unchecked((short)0xFFFE) : (short)(isFloat ? 3 : 1));
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bytes);
        writer.Write((short)(channels * bytes));
        writer.Write((short)(bytes * 8));
        if (extensible)
        {
            writer.Write((short)22);
            writer.Write((short)(bytes * 8));
            writer.Write(0);
            writer.Write((short)(isFloat ? 3 : 1));
            writer.Write(new byte[14]);
        }

        writer.Write("data"u8);
        writer.Write(frames * channels * bytes);
        for (var i = 0; i < frames; i++)
        {
            var value = Math.Sin(2 * Math.PI * 440 * i / rate) * 0.25;
            for (var c = 0; c < channels; c++)
            {
                if (isFloat)
                {
                    writer.Write((float)value);
                }
                else
                {
                    writer.Write((short)(value * 32767));
                }
            }
        }

        writer.Flush();
        return output.ToArray();
    }

    /// <summary>Число переходов через ноль вверх — частота тона не должна «уплыть» при пересчёте.</summary>
    private static int Periods(float[] pcm) => Enumerable.Range(1, pcm.Length - 1).Count(i => pcm[i - 1] < 0 && pcm[i] >= 0);

    [Fact]
    public void Wav_16_khz_mono_is_taken_as_is()
    {
        var pcm = WavDecoder.ToWhisperPcm(WavTone(2));

        Assert.Equal(32_000, pcm.Length);
        Assert.InRange(pcm.Max(), 0.24f, 0.26f);
        Assert.InRange(pcm.Min(), -0.26f, -0.24f);
        Assert.InRange(Periods(pcm), 878, 881);
    }

    [Theory]
    [InlineData(48_000, 2, false, false)]
    [InlineData(44_100, 1, false, false)]
    [InlineData(44_100, 2, true, false)]
    [InlineData(8_000, 1, false, false)]
    [InlineData(48_000, 2, true, true)]
    public void Wav_of_another_rate_channel_count_or_sample_type_becomes_16_khz_mono(int rate, int channels, bool isFloat, bool extensible)
    {
        var pcm = WavDecoder.ToWhisperPcm(WavTone(2, rate, channels, isFloat, extensible));

        Assert.InRange(pcm.Length, 31_990, 32_000);
        Assert.InRange(pcm.Max(), 0.2f, 0.26f);
        Assert.InRange(Periods(pcm), 876, 882);
        Assert.All(pcm, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void A_wav_with_an_unfinished_data_size_is_read_to_the_end_of_the_file()
    {
        var wav = WavTone(1);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, wav.Length - 32_000 - 4);

        Assert.Equal(16_000, WavDecoder.ToWhisperPcm(wav).Length);
    }

    [Fact]
    public void A_broken_or_unsupported_wav_is_refused()
    {
        var noData = WavTone(1)[..40];
        var eightBit = WavTone(1);
        eightBit[12 + 12 + 8 + 14] = 8;

        Assert.Throws<InvalidDataException>(() => WavDecoder.ToWhisperPcm("RIFF____AVI "u8));
        Assert.Throws<InvalidDataException>(() => WavDecoder.ToWhisperPcm(noData));
        Assert.Throws<NotSupportedException>(() => WavDecoder.ToWhisperPcm(eightBit));
    }

    [Fact]
    public async Task Wav_goes_the_same_way_as_ogg_and_is_checked_before_the_model()
    {
        var options = new SpeechOptions { ModelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin"), MaxSeconds = 3 };
        using var speech = new WhisperSpeechToText(options, NullLogger<WhisperSpeechToText>.Instance);

        // Короткая запись — тишина; обычная доходит до модели, которой нет; слишком длинная и битая отклоняются.
        Assert.Equal(string.Empty, await speech.TranscribeAsync(new MemoryStream(WavTone(0.1)), Ct));
        var noModel = await Assert.ThrowsAsync<SpeechException>(() => speech.TranscribeAsync(new MemoryStream(WavTone(1)), Ct));
        Assert.Contains("Нет модели", noModel.Message, StringComparison.Ordinal);
        var tooLong = await Assert.ThrowsAsync<SpeechException>(() => speech.TranscribeAsync(new MemoryStream(WavTone(5)), Ct));
        Assert.Contains("длиннее", tooLong.Message, StringComparison.Ordinal);
        var broken = await Assert.ThrowsAsync<SpeechException>(() => speech.TranscribeAsync(new MemoryStream("RIFF-and-nothing-else"u8.ToArray()), Ct));
        Assert.IsType<InvalidDataException>(broken.InnerException);
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
