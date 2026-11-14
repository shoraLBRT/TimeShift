using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TimeShift.Application.Abstractions;

namespace TimeShift.Infrastructure.Llm;

/// <summary>
/// <c>POST {BaseUrl}/chat/completions</c> в формате OpenAI. Одна повторная попытка на временные ошибки
/// (сеть, таймаут, 429, 5xx, ответ не JSON); ответ должен быть JSON-объектом — иначе <see cref="LlmException"/>.
/// </summary>
internal sealed class OpenAiCompatibleLlmProvider(HttpClient http, LlmOptions options, TimeSpan? retryDelay = null) : ILlmProvider
{
    private const int Attempts = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);

    public bool IsEnabled => options.IsEnabled;

    public async Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsEnabled)
        {
            throw new LlmException("LLM выключен: не задан Llm:ApiKey.", isTransient: false);
        }

        var body = new ChatRequest(
            options.Model,
            [new ChatMessage("system", request.SystemPrompt), new ChatMessage("user", request.UserMessage)],
            options.Temperature,
            options.MaxTokens,
            options.JsonMode ? new ResponseFormat("json_object") : null);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendAsync(body, cancellationToken);
            }
            catch (LlmException e) when (e.IsTransient && attempt < Attempts)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }
    }

    private async Task<LlmResponse> SendAsync(ChatRequest body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"LLM не ответил за {options.TimeoutSeconds} с.", isTransient: true, e);
        }
        catch (HttpRequestException e)
        {
            throw new LlmException("Нет связи с LLM.", isTransient: true, e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                    || (int)response.StatusCode >= 500;
                throw new LlmException($"LLM вернул {(int)response.StatusCode} {response.ReasonPhrase}.", transient);
            }

            ChatResponse? parsed;
            try
            {
                parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, timeout.Token);
            }
            catch (JsonException e)
            {
                throw new LlmException("Ответ LLM — не JSON протокола chat/completions.", isTransient: true, e);
            }

            var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new LlmException("LLM вернул пустой ответ.", isTransient: true);
            }

            content = StripCodeFence(content);
            if (!IsJsonObject(content))
            {
                throw new LlmException("LLM вернул не JSON-объект.", isTransient: true);
            }

            return new LlmResponse(content, parsed!.Usage?.PromptTokens, parsed.Usage?.CompletionTokens);
        }
    }

    /// <summary>Некоторые модели оборачивают JSON в <c>```json … ```</c> даже в JSON-режиме.</summary>
    private static string StripCodeFence(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n', StringComparison.Ordinal);
        var closing = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd > 0 && closing > firstLineEnd ? text[(firstLineEnd + 1)..closing].Trim() : text;
    }

    private static bool IsJsonObject(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ChatRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat);

    private sealed record ChatMessage(string Role, string? Content);

    private sealed record ResponseFormat(string Type);

    private sealed record ChatResponse(IReadOnlyList<Choice>? Choices, Usage? Usage);

    private sealed record Choice(ChatMessage? Message);

    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
}
