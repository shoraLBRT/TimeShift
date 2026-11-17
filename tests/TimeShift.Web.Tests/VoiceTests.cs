using System.Buffers.Binary;
using System.Net;
using TimeShift.Application.Abstractions;

namespace TimeShift.Web.Tests;

/// <summary>Голос из PWA: <c>POST /api/messages</c> с телом <c>audio/wav</c> (#50). Распознавание подменено.</summary>
public class VoiceTests
{
    /// <summary>WAV 16 кГц, моно, 16 бит — тон 440 Гц, как присылает клиент.</summary>
    private static byte[] Wav(double seconds)
    {
        const int Rate = 16_000;
        var samples = (int)(Rate * seconds);
        var wav = new byte[44 + (samples * 2)];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), samples * 2);
        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (i * 2)), (short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * 8000));
        }

        return wav;
    }

    private static string ReplyText(ApiResponse response, int index) => response.Json["replies"]![index]!["text"]!.GetValue<string>();

    [Fact]
    public async Task Voice_is_recognised_and_handled_like_text_and_the_reply_starts_with_what_was_heard()
    {
        await using var app = new TestApp();
        app.Speech.Enabled = true;
        app.Speech.Text = "Тренировка";
        await app.OnboardAsync();
        var wav = Wav(12);

        var response = await app.PostAudioAsync("/api/messages", wav);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("🎙 «Тренировка»", ReplyText(response, 0));
        Assert.Contains("Тренировка", ReplyText(response, 1), StringComparison.Ordinal);
        Assert.Equal("Тренировка", response.Json["day"]!["current"]!["title"]!.GetValue<string>());
        Assert.Equal(wav, Assert.Single(app.Speech.Received));
    }

    [Fact]
    public async Task Content_type_with_parameters_and_the_view_day_work_for_voice_too()
    {
        await using var app = new TestApp();
        app.Speech.Enabled = true;
        app.Speech.Text = "Читаю";
        await app.OnboardAsync();

        var response = await app.PostAudioAsync("/api/messages?view=2026-11-16", Wav(1), "audio/x-wav; codecs=1");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("🎙 «Читаю»", ReplyText(response, 0));
        Assert.Equal("2026-11-16", response.Json["day"]!["date"]!.GetValue<string>());
    }

    [Fact]
    public async Task Silence_and_a_recognition_failure_are_ordinary_replies_and_change_nothing()
    {
        await using var app = new TestApp();
        app.Speech.Enabled = true;
        await app.OnboardAsync();

        app.Speech.Text = "  ";
        var silence = await app.PostAudioAsync("/api/messages", Wav(2));
        Assert.Equal(HttpStatusCode.OK, silence.Status);
        Assert.Contains("Не расслышал", ReplyText(silence, 0), StringComparison.Ordinal);
        Assert.Single(silence.Json["replies"]!.AsArray());

        app.Speech.Failure = new SpeechException("boom");
        var failed = await app.PostAudioAsync("/api/messages", Wav(2));
        Assert.Equal(HttpStatusCode.OK, failed.Status);
        Assert.Contains("Не получилось распознать", ReplyText(failed, 0), StringComparison.Ordinal);
        Assert.Null(failed.Json["day"]!["current"]);
    }

    [Fact]
    public async Task Without_a_model_meta_says_so_and_voice_is_refused()
    {
        await using var app = new TestApp();
        await app.OnboardAsync();

        var meta = await app.GetAsync("/api/meta");
        Assert.False(meta.Json["speech"]!.GetValue<bool>());

        var response = await app.PostAudioAsync("/api/messages", Wav(1));
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Empty(app.Speech.Received);

        app.Speech.Enabled = true;
        app.Speech.MaxDuration = TimeSpan.FromSeconds(90);
        meta = await app.GetAsync("/api/meta");
        Assert.True(meta.Json["speech"]!.GetValue<bool>());
        Assert.Equal(90, meta.Json["speechMaxSec"]!.GetValue<int>());
    }

    [Fact]
    public async Task Too_long_empty_and_non_wav_recordings_are_rejected_before_recognition()
    {
        await using var app = new TestApp();
        app.Speech.Enabled = true;
        app.Speech.MaxDuration = TimeSpan.FromSeconds(5);
        await app.OnboardAsync();

        var atTheLimit = await app.PostAudioAsync("/api/messages", Wav(5));
        Assert.Equal(HttpStatusCode.OK, atTheLimit.Status);
        Assert.Single(app.Speech.Received);

        var tooLong = await app.PostAudioAsync("/api/messages", Wav(5.5));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.Status);
        Assert.Equal("audio", tooLong.Json["field"]!.GetValue<string>());
        Assert.Contains("длиннее", tooLong.Json["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // Запись намного больше предела: сервер не читает её целиком и отвечает тем же.
        var huge = await app.PostAudioAsync("/api/messages", Wav(60));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, huge.Status);

        var empty = await app.PostAudioAsync("/api/messages", []);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.Status);
        Assert.Equal("audio", empty.Json["field"]!.GetValue<string>());

        var notWav = await app.PostAudioAsync("/api/messages", "OggS-not-a-wav-file-but-long-enough-to-pass-the-length-check-of-a-header"u8.ToArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notWav.Status);

        Assert.Single(app.Speech.Received);
    }

    [Fact]
    public async Task Voice_needs_a_session_the_csrf_header_and_a_finished_onboarding()
    {
        await using var app = new TestApp();
        app.Speech.Enabled = true;
        app.Speech.Text = "Работаю";
        static ByteArrayContent Audio()
        {
            var content = new ByteArrayContent(Wav(1));
            content.Headers.ContentType = new("audio/wav");
            return content;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.SendAsync(HttpMethod.Post, "/api/messages", Audio(), authenticated: false)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.SendAsync(HttpMethod.Post, "/api/messages", Audio(), csrf: false)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAudioAsync("/api/messages", Wav(1))).Status);
        Assert.Empty(app.Speech.Received);
    }
}
