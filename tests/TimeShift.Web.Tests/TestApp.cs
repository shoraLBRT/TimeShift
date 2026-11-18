using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TimeShift.Application.Abstractions;
using TimeShift.TestSupport;
using TimeShift.Web;

namespace TimeShift.Web.Tests;

internal sealed class FakeClock : IClock
{
    /// <summary>2026-11-17 11:30 по Москве.</summary>
    public DateTime UtcNow { get; set; } = new(2026, 11, 17, 8, 30, 0, DateTimeKind.Utc);
}

/// <summary>Вместо настоящего push-сервиса: запоминает сообщения, отвечает как скажут.</summary>
internal sealed class FakeWebPushSender : IWebPushSender
{
    public List<(PushRequest Request, VapidKeys Keys)> Sent { get; } = [];

    public HashSet<string> GoneEndpoints { get; } = [];

    public HashSet<string> FailingEndpoints { get; } = [];

    public Task<PushOutcome> SendAsync(PushRequest request, VapidKeys keys, CancellationToken cancellationToken)
    {
        if (FailingEndpoints.Contains(request.Endpoint))
        {
            throw new InvalidOperationException("push service is down");
        }

        Sent.Add((request, keys));
        return Task.FromResult(GoneEndpoints.Contains(request.Endpoint) ? PushOutcome.Gone : PushOutcome.Delivered);
    }
}

/// <summary>Вместо whisper: по умолчанию выключено (как сервер без модели); запоминает присланные записи.</summary>
internal sealed class FakeSpeech : ISpeechToText
{
    public bool Enabled { get; set; }

    public string Text { get; set; } = string.Empty;

    public Exception? Failure { get; set; }

    public List<byte[]> Received { get; } = [];

    public bool IsEnabled => Enabled;

    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(120);

    public async Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        await audio.CopyToAsync(copy, cancellationToken);
        Received.Add(copy.ToArray());
        return Failure is not null ? throw Failure : Text;
    }
}

/// <summary>Вместо LLM: выключен, пока тест не положил ответ; ответы отдаёт по очереди.</summary>
internal sealed class FakeLlm : ILlmProvider
{
    private readonly Queue<string> _responses = new();

    public bool IsEnabled { get; private set; }

    /// <summary>Следующий ответ модели — JSON с намерениями; с первого же ответа «LLM включён».</summary>
    public void Enqueue(string json)
    {
        IsEnabled = true;
        _responses.Enqueue(json);
    }

    public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken) =>
        _responses.Count > 0
            ? Task.FromResult(new LlmResponse(_responses.Dequeue()))
            : throw new LlmException("нет ответа", false);
}

internal sealed record ApiResponse(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Raw)
{
    public JsonNode Json => Body ?? throw new InvalidOperationException($"No JSON body ({(int)Status}).");

    public string? Error => Body?["error"]?.GetValue<string>();
}

/// <summary>Приложение на временной базе (<see cref="TestDatabase"/>: SQLite или Postgres) с настраиваемыми часами; по умолчанию уже «вошло» (cookie подделан тем же алгоритмом).</summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string AccessKey = "test-key";

    private readonly TestDatabase _database;

    private readonly bool _ownsDatabase;

    private readonly WebApplicationFactory<Program> _factory;

    private readonly HttpClient _client;

    public TestApp(string accessKey = AccessKey, string? telegramChatId = null, TestDatabase? database = null, Dictionary<string, string?>? extra = null)
    {
        _ownsDatabase = database is null;
        _database = database ?? TestDatabase.Create();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", _database.Provider);
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("ConnectionStrings:Default", _database.ConnectionString);
            builder.UseSetting("Web:AccessKey", accessKey);
            builder.UseSetting("Web:TelegramChatId", telegramChatId);
            builder.UseSetting("Telegram:BotToken", string.Empty);
            foreach (var (key, value) in extra ?? [])
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(Clock);
                services.RemoveAll<IWebPushSender>();
                services.AddSingleton<IWebPushSender>(Push);
                services.RemoveAll<ISpeechToText>();
                services.AddSingleton<ISpeechToText>(Speech);
                services.RemoveAll<ILlmProvider>();
                services.AddSingleton<ILlmProvider>(Llm);
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
    }

    public FakeClock Clock { get; } = new();

    public FakeWebPushSender Push { get; } = new();

    public FakeSpeech Speech { get; } = new();

    public FakeLlm Llm { get; } = new();

    public IServiceProvider Services => _factory.Services;

    public string SessionCookie => $"{SessionTokens.CookieName}={SessionTokens.Create(AccessKey, Clock.UtcNow + SessionTokens.Lifetime)}";

    /// <summary>Запрос; по умолчанию с сессией и заголовком CSRF для изменяющих методов.</summary>
    public async Task<ApiResponse> SendAsync(
        HttpMethod method, string path, object? body = null, bool authenticated = true, bool csrf = true, string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is HttpContent payload)
        {
            request.Content = payload;
        }
        else if (body is not null)
        {
            var json = body is string text ? text : System.Text.Json.JsonSerializer.Serialize(body, ApiJson.Options);
            request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (csrf && method != HttpMethod.Get)
        {
            request.Headers.Add("X-TimeShift", "1");
        }

        if (cookie is not null || authenticated)
        {
            request.Headers.Add("Cookie", cookie ?? SessionCookie);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new ApiResponse(response.StatusCode, content.Length == 0 ? null : JsonNode.Parse(content), response);
    }

    public Task<ApiResponse> GetAsync(string path, bool authenticated = true) => SendAsync(HttpMethod.Get, path, null, authenticated);

    public Task<ApiResponse> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

    /// <summary>Тело — запись целиком, как её шлёт PWA.</summary>
    public Task<ApiResponse> PostAudioAsync(string path, byte[] audio, string contentType = "audio/wav")
    {
        var content = new ByteArrayContent(audio);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return SendAsync(HttpMethod.Post, path, content);
    }

    public Task<ApiResponse> PatchAsync(string path, object body) => SendAsync(HttpMethod.Patch, path, body);

    public Task<ApiResponse> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);

    public Task<ApiResponse> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    /// <summary>Сон 00:30–08:30 и конец онбординга.</summary>
    public async Task OnboardAsync()
    {
        Assert.Equal(HttpStatusCode.OK, (await PutAsync("/api/schedule/sleep", new { start = "00:30", end = "08:30", weekend = (object?)null })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/onboarding/complete")).Status);
    }

    /// <summary>Местное (Москва) время на сегодняшнюю дату часов: <c>Local(9, 0)</c> → <c>2026-11-17T09:00</c>.</summary>
    public string Local(int hour, int minute, int dayOffset = 0) =>
        $"{DateOnly.FromDateTime(Clock.UtcNow.AddHours(3)).AddDays(dayOffset):yyyy-MM-dd}T{hour:00}:{minute:00}";

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (_ownsDatabase)
        {
            await _database.DisposeAsync();
        }
    }
}
