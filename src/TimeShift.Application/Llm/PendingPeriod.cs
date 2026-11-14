namespace TimeShift.Application.Llm;

/// <summary>
/// Разобранный, но ещё не записанный период (spec §3.5 п. 7): ответ модели хранится целиком, при подтверждении
/// раскладка пересчитывается заново с тем же «сейчас» — результат тот же, но уже с актуальными фактами.
/// <see cref="FactKey"/> задан — ждём ответ на вопрос о факте; иначе — подтверждение предложения.
/// </summary>
public sealed class PendingPeriod
{
    public PendingPeriod(Guid userId, DateTime createdAtUtc, string text, string responseJson, int intentIndex, string? factKey, string? question)
    {
        UserId = userId;
        CreatedAtUtc = createdAtUtc;
        Text = text;
        ResponseJson = responseJson;
        IntentIndex = intentIndex;
        FactKey = factKey;
        Question = question;
    }

    // Для EF Core.
    private PendingPeriod()
    {
        Text = string.Empty;
        ResponseJson = string.Empty;
    }

    public Guid UserId { get; private set; }

    /// <summary>Когда разобрано; это же «сейчас» для пересчёта.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    public string Text { get; private set; }

    public string ResponseJson { get; private set; }

    public int IntentIndex { get; private set; }

    public string? FactKey { get; private set; }

    public string? Question { get; private set; }

    public bool IsWaitingForFact => FactKey is not null;
}

/// <summary>Строка журнала разборов LLM.</summary>
public sealed class MessageLogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid UserId { get; init; }

    public required DateTime AtUtc { get; init; }

    public required string Text { get; init; }

    public required string Status { get; init; }

    /// <summary>Последний ответ модели (JSON) или ошибка.</summary>
    public string? Response { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }
}
