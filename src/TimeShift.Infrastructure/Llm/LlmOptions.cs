using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace TimeShift.Infrastructure.Llm;

/// <summary>
/// Настройки LLM (секция <c>Llm</c>; env <c>Llm__ApiKey</c> и т. д.). OpenAI-совместимый API: ruble-агрегаторы
/// (ProxyAPI, AITunnel, Polza) и сами провайдеры. Пустой ключ — LLM выключен.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>База API без <c>/chat/completions</c>, например <c>https://api.proxyapi.ru/openai/v1</c>.</summary>
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    public string? ApiKey { get; init; }

    public string Model { get; init; } = "gpt-4o-mini";

    public double Temperature { get; init; }

    public int MaxTokens { get; init; } = 800;

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Просить у API <c>response_format: json_object</c>. Выключить для провайдеров, которые его не знают.</summary>
    public bool JsonMode { get; init; } = true;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ApiKey);

    public static LlmOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new LlmOptions();
        return new LlmOptions
        {
            BaseUrl = NonEmpty(section[nameof(BaseUrl)]) ?? defaults.BaseUrl,
            ApiKey = NonEmpty(section[nameof(ApiKey)]),
            Model = NonEmpty(section[nameof(Model)]) ?? defaults.Model,
            Temperature = double.TryParse(section[nameof(Temperature)], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                ? t : defaults.Temperature,
            MaxTokens = int.TryParse(section[nameof(MaxTokens)], CultureInfo.InvariantCulture, out var m) ? m : defaults.MaxTokens,
            TimeoutSeconds = int.TryParse(section[nameof(TimeoutSeconds)], CultureInfo.InvariantCulture, out var s) ? s : defaults.TimeoutSeconds,
            JsonMode = !bool.TryParse(section[nameof(JsonMode)], out var j) || j,
        };
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
