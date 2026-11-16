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
using TimeShift.Web;

namespace TimeShift.Web.Tests;

internal sealed class FakeClock : IClock
{
    /// <summary>2026-11-17 11:30 по Москве.</summary>
    public DateTime UtcNow { get; set; } = new(2026, 11, 17, 8, 30, 0, DateTimeKind.Utc);
}

internal sealed record ApiResponse(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Raw)
{
    public JsonNode Json => Body ?? throw new InvalidOperationException($"No JSON body ({(int)Status}).");

    public string? Error => Body?["error"]?.GetValue<string>();
}

/// <summary>Приложение на временной SQLite-базе с настраиваемыми часами; по умолчанию уже «вошло» (cookie подделан тем же алгоритмом).</summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string AccessKey = "test-key";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"timeshift-web-{Guid.NewGuid():N}.db");

    private readonly WebApplicationFactory<Program> _factory;

    private readonly HttpClient _client;

    public TestApp(string accessKey = AccessKey, string? telegramChatId = null)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
            builder.UseSetting("Web:AccessKey", accessKey);
            builder.UseSetting("Web:TelegramChatId", telegramChatId);
            builder.UseSetting("Telegram:BotToken", string.Empty);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(Clock);
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
    }

    public FakeClock Clock { get; } = new();

    public IServiceProvider Services => _factory.Services;

    public string SessionCookie => $"{SessionTokens.CookieName}={SessionTokens.Create(AccessKey, Clock.UtcNow + SessionTokens.Lifetime)}";

    /// <summary>Запрос; по умолчанию с сессией и заголовком CSRF для изменяющих методов.</summary>
    public async Task<ApiResponse> SendAsync(
        HttpMethod method, string path, object? body = null, bool authenticated = true, bool csrf = true, string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
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
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Временный файл — не страшно.
            }
        }
    }
}
