namespace TimeShift.Infrastructure.Persistence;

/// <summary>Строка журнала изменений ленты (spec §7: timeline_changes); отрезки — в JSON.</summary>
public sealed class TimelineChangeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>
    /// Когда записано — и порядок <c>/undo</c>: у одного пользователя строго растёт (изменения одного сообщения
    /// идут через микросекунду друг за другом, см. <c>TimelineRepository.NextJournalTime</c>).
    /// </summary>
    public DateTime AtUtc { get; set; }

    public string BeforeJson { get; set; } = "[]";

    public string AfterJson { get; set; } = "[]";
}
