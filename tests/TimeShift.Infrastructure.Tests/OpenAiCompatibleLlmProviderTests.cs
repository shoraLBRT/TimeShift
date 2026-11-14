using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TimeShift.Application.Abstractions;
using TimeShift.Infrastructure.Llm;

namespace TimeShift.Infrastructure.Tests;

/// <summary>Адаптер на записанных ответах chat/completions — без сети и ключей.</summary>
public class OpenAiCompatibleLlmProviderTests
{
    private static readonly LlmOptions Options = new()
    {
        BaseUrl = "https://llm.example/v1/",
        ApiKey = "test-key",
        Model = "deepseek-chat",
        TimeoutSeconds = 5,
    };

    private static readonly LlmRequest Request = new("Верни JSON с намерениями.", "тренировался час");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Completion(string content, int prompt = 120, int completion = 30) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = prompt, completion_tokens = completion, total_tokens = prompt + completion },
    });

    private static (OpenAiCompatibleLlmProvider Provider, StubHandler Handler) Create(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new StubHandler(responses);
        return (new OpenAiCompatibleLlmProvider(new HttpClient(handler), Options, TimeSpan.Zero), handler);
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Sends_an_openai_request_and_returns_json_with_usage()
    {
        var (provider, handler) = Create(_ => Ok(Completion("""{"intents":[{"type":"stop_activity"}]}""")));

        var response = await provider.CompleteJsonAsync(Request, Ct);

        Assert.Equal("""{"intents":[{"type":"stop_activity"}]}""", response.Content);
        Assert.Equal((120, 30), (response.PromptTokens!.Value, response.CompletionTokens!.Value));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://llm.example/v1/chat/completions", sent.Uri);
        Assert.Equal("Bearer test-key", sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(800, root.GetProperty("max_tokens").GetInt32());
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("тренировался час", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Code_fences_around_json_are_removed()
    {
        var (provider, _) = Create(_ => Ok(Completion("```json\n{\"intents\":[]}\n```")));

        var response = await provider.CompleteJsonAsync(Request, Ct);

        Assert.Equal("""{"intents":[]}""", response.Content);
    }

    [Fact]
    public async Task Transient_errors_are_retried_once()
    {
        var (provider, handler) = Create(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Ok(Completion("""{"intents":[]}""")));

        var response = await provider.CompleteJsonAsync(Request, Ct);

        Assert.Equal("""{"intents":[]}""", response.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Not_a_json_object_is_retried_then_fails_as_transient()
    {
        var (provider, handler) = Create(_ => Ok(Completion("Конечно! Вот намерения: тренировка.")));

        var error = await Assert.ThrowsAsync<LlmException>(() => provider.CompleteJsonAsync(Request, Ct));

        Assert.True(error.IsTransient);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Auth_errors_are_not_retried()
    {
        var (provider, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<LlmException>(() => provider.CompleteJsonAsync(Request, Ct));

        Assert.False(error.IsTransient);
        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Network_failure_becomes_a_transient_llm_error()
    {
        var (provider, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<LlmException>(() => provider.CompleteJsonAsync(Request, Ct));

        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Without_a_key_the_provider_is_disabled()
    {
        var handler = new StubHandler([]);
        var provider = new OpenAiCompatibleLlmProvider(new HttpClient(handler), new LlmOptions(), TimeSpan.Zero);

        Assert.False(provider.IsEnabled);
        await Assert.ThrowsAsync<LlmException>(() => provider.CompleteJsonAsync(Request, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Options_are_read_from_configuration_with_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:BaseUrl"] = "https://api.proxyapi.ru/openai/v1",
                ["Llm:ApiKey"] = "k",
                ["Llm:Model"] = "gpt-5-mini",
                ["Llm:JsonMode"] = "false",
            })
            .Build();

        var options = LlmOptions.From(configuration);

        Assert.True(options.IsEnabled);
        Assert.Equal("gpt-5-mini", options.Model);
        Assert.False(options.JsonMode);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.False(LlmOptions.From(new ConfigurationBuilder().Build()).IsEnabled);
    }

    private sealed record SentRequest(string Uri, string? Authorization, string Body);

    private sealed class StubHandler(IReadOnlyList<Func<HttpRequestMessage, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            var respond = responses[Math.Min(Requests.Count - 1, responses.Count - 1)];
            return respond(request);
        }
    }
}
