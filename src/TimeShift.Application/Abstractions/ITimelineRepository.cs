using TimeShift.Domain.Timelines;

namespace TimeShift.Application.Abstractions;

/// <summary>Изменение ленты из журнала (spec §7: timeline_changes) — для <c>/undo</c>.</summary>
public sealed record StoredTimelineChange(Guid Id, DateTime AtUtc, TimelineChange Change);

/// <summary>Материализованные отрезки пользователя и журнал их изменений.</summary>
public interface ITimelineRepository
{
    /// <summary>Отрезки, которые кончаются после <paramref name="fromUtc"/> (и открытый), обе дорожки.</summary>
    Task<Timeline> LoadAsync(Guid userId, DateTime fromUtc, CancellationToken cancellationToken);

    Task<Segment?> FindOpenAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Сохраняет изменение (убирает <c>Before</c>, добавляет <c>After</c>) и пишет его в журнал.</summary>
    void Record(Guid userId, TimelineChange change, DateTime atUtc);

    Task<StoredTimelineChange?> LastChangeAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Откатывает изменение из журнала в хранилище и удаляет его из журнала.</summary>
    Task RevertAsync(Guid userId, StoredTimelineChange change, CancellationToken cancellationToken);
}
