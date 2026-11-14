using TimeShift.Application.Abstractions;

namespace TimeShift.Application.Llm;

public enum ExtractionStatus
{
    /// <summary>Намерения извлечены и прошли проверку.</summary>
    Ok,

    /// <summary>LLM не настроен — бот работает без него.</summary>
    Disabled,

    /// <summary>Модель дважды вернула ответ не по контракту — «Не понял» с кнопками.</summary>
    Invalid,

    /// <summary>Провайдер недоступен (сеть, лимит, ключ).</summary>
    Unavailable,
}

/// <summary>Итог извлечения; токены — сумма по всем попыткам (для учёта цены).</summary>
public sealed record IntentExtraction(
    ExtractionStatus Status,
    IReadOnlyList<Intent> Intents,
    IReadOnlyList<string> Errors,
    int PromptTokens,
    int CompletionTokens,
    IReadOnlyList<string> RawResponses);

/// <summary>
/// Текст → намерения через LLM (spec §8): промпт с контекстом, строгая проверка, при ошибке — одна повторная
/// попытка с перечнем ошибок, потом <see cref="ExtractionStatus.Invalid"/>.
/// </summary>
public sealed class IntentExtractor(ILlmProvider llm)
{
    private const int Attempts = 2;

    public async Task<IntentExtraction> ExtractAsync(string text, IntentContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!llm.IsEnabled)
        {
            return new IntentExtraction(ExtractionStatus.Disabled, [], [], 0, 0, []);
        }

        var system = IntentPrompt.Build(context);
        var message = text.Trim();
        var raw = new List<string>();
        var (prompt, completion) = (0, 0);
        IReadOnlyList<string> errors = [];

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            LlmResponse response;
            try
            {
                response = await llm.CompleteJsonAsync(new LlmRequest(system, message), cancellationToken);
            }
            catch (LlmException e)
            {
                return new IntentExtraction(ExtractionStatus.Unavailable, [], [e.Message], prompt, completion, raw);
            }

            raw.Add(response.Content);
            prompt += response.PromptTokens ?? 0;
            completion += response.CompletionTokens ?? 0;

            var parsed = IntentParser.Parse(response.Content);
            if (parsed.IsValid)
            {
                return new IntentExtraction(ExtractionStatus.Ok, parsed.Intents, [], prompt, completion, raw);
            }

            errors = parsed.Errors;
            message = $"""
                {text.Trim()}

                (Твой прошлый ответ не прошёл проверку: {string.Join("; ", errors)}. Верни исправленный JSON по контракту.)
                """;
        }

        return new IntentExtraction(ExtractionStatus.Invalid, [], errors, prompt, completion, raw);
    }
}
