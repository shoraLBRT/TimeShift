using System.Buffers.Binary;

namespace TimeShift.Speech;

/// <summary>
/// WAV (RIFF) → PCM 16 кГц моно float для whisper.cpp. Web-клиент присылает уже 16 кГц моно 16 бит, но разбор не
/// привязан к этому: PCM 16 бит и float 32 бит, любое число каналов (усредняются) и частота (пересчитывается).
/// </summary>
internal static class WavDecoder
{
    private const int FormatPcm = 1;

    private const int FormatFloat = 3;

    private const int FormatExtensible = 0xFFFE;

    public static bool LooksLikeWav(ReadOnlySpan<byte> start) => start.Length >= 4 && start[..4].SequenceEqual("RIFF"u8);

    public static float[] ToWhisperPcm(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        var position = 12;
        while (position + 8 <= data.Length)
        {
            var id = data.Slice(position, 4);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            position += 8;

            // Размер блока может быть не дописан (запись потоком) — берём то, что есть.
            var size = (int)Math.Min(declared, (uint)(data.Length - position));
            var chunk = data.Slice(position, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16)
                {
                    throw new InvalidDataException("The fmt chunk is too short.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                if (format == FormatExtensible && size >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(chunk[24..]);
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException("The data chunk comes before fmt.");
                }

                return Resample(Mono(chunk, format, channels, bits), rate);
            }

            position += size + (size & 1);
        }

        throw new InvalidDataException("No data chunk.");
    }

    private static float[] Mono(ReadOnlySpan<byte> data, int format, int channels, int bits)
    {
        var bytes = (format, bits) switch
        {
            (FormatPcm, 16) => 2,
            (FormatFloat, 32) => 4,
            _ => throw new NotSupportedException($"WAV format {format} with {bits} bits is not supported."),
        };
        if (channels is < 1 or > 8)
        {
            throw new NotSupportedException($"WAV with {channels} channels is not supported.");
        }

        var frame = bytes * channels;
        var result = new float[data.Length / frame];
        for (var i = 0; i < result.Length; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var sample = data.Slice((i * frame) + (c * bytes), bytes);
                sum += bytes == 2 ? BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f : BinaryPrimitives.ReadSingleLittleEndian(sample);
            }

            var value = sum / channels;
            result[i] = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
        }

        return result;
    }

    /// <summary>
    /// К 16 кГц: вниз — среднее по окну исходных отсчётов (заодно фильтр нижних частот), вверх — линейная
    /// интерполяция. Для речи достаточно.
    /// </summary>
    private static float[] Resample(float[] source, int rate)
    {
        const int Target = OggOpusDecoder.WhisperRate;
        if (rate is < 4000 or > 192_000)
        {
            throw new NotSupportedException($"WAV sample rate {rate} is not supported.");
        }

        if (rate == Target || source.Length == 0)
        {
            return source;
        }

        var result = new float[(int)((long)source.Length * Target / rate)];
        var step = (double)rate / Target;
        for (var i = 0; i < result.Length; i++)
        {
            var from = i * step;
            if (step > 1)
            {
                var start = (int)from;
                var end = Math.Min(source.Length, Math.Max(start + 1, (int)((i + 1) * step)));
                var sum = 0f;
                for (var j = start; j < end; j++)
                {
                    sum += source[j];
                }

                result[i] = sum / (end - start);
            }
            else
            {
                var left = Math.Min((int)from, source.Length - 1);
                var right = Math.Min(left + 1, source.Length - 1);
                var t = (float)(from - left);
                result[i] = source[left] + ((source[right] - source[left]) * t);
            }
        }

        return result;
    }
}
