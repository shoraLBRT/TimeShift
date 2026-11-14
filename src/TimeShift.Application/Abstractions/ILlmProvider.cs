namespace TimeShift.Application.Abstractions;

/// <summary>Запрос к LLM: системный промпт (контракт и контекст) и сообщение пользователя.</summary>
public sealed record LlmRequest(string SystemPrompt, string UserMessage);

/// <summary>Ответ модели — строка с JSON-объектом; токены — для учёта цены.</summary>
public sealed record LlmResponse(string Content, int? PromptTokens = null, int? CompletionTokens = null);

/// <summary>Провайдер не ответил или ответил не по протоколу. <see cref="IsTransient"/> — можно повторить позже.</summary>
public sealed class LlmException(string message, bool isTransient, Exception? inner = null) : Exception(message, inner)
{
    public bool IsTransient { get; } = isTransient;
}

/// <summary>
/// LLM, который только извлекает намерения в строгий JSON (spec §8). Провайдер задаётся конфигурацией;
/// без ключа <see cref="IsEnabled"/> = <c>false</c> и бот работает без LLM (кнопки и короткий текст).
/// </summary>
public interface ILlmProvider
{
    bool IsEnabled { get; }

    /// <summary>Возвращает JSON-объект строкой. Бросает <see cref="LlmException"/>.</summary>
    Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken);
}
