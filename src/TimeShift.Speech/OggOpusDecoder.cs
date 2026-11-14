using Concentus;
using Concentus.Oggfile;

namespace TimeShift.Speech;

/// <summary>
/// Голосовое Telegram (OGG/Opus, моно) → PCM 16 кГц float для whisper.cpp. Чистый C# (Concentus) — без FFmpeg
/// на сервере.
/// </summary>
internal static class OggOpusDecoder
{
    private const int OpusRate = 48_000;

    public const int WhisperRate = 16_000;

    private const int Factor = OpusRate / WhisperRate;

    public static float[] ToWhisperPcm(Stream oggOpus)
    {
        ArgumentNullException.ThrowIfNull(oggOpus);
        var decoder = OpusCodecFactory.CreateDecoder(OpusRate, 1);
        var reader = new OpusOggReadStream(decoder, oggOpus);
        var samples48 = new List<short>();
        while (reader.HasNextPacket)
        {
            var packet = reader.DecodeNextPacket();
            if (packet is not null)
            {
                samples48.AddRange(packet);
            }
        }

        // 48 → 16 кГц: среднее по трём отсчётам — простой фильтр нижних частот, для речи достаточно.
        var result = new float[samples48.Count / Factor];
        for (var i = 0; i < result.Length; i++)
        {
            var sum = 0;
            for (var j = 0; j < Factor; j++)
            {
                sum += samples48[(i * Factor) + j];
            }

            result[i] = sum / (Factor * 32768f);
        }

        return result;
    }
}
