using TimeShift.Domain.Timelines;

namespace TimeShift.Application.Abstractions;

/// <summary>Изменение ленты из журнала (spec §7: timeline_changes) — для <c>/undo</c>.</summary>
public sealed record StoredTimelineChange(Guid Id, DateTime AtUtc, TimelineChange Change);

/// <summary>
/// Материализованные отрезки пользователя и журнал их изменений. Сохраняет вызывающий (<see cref="IUnitOfWork"/>),
/// один раз на сообщение, поэтому чтения обязаны видеть и то, что эта же единица работы записала, но ещё не
/// сохранила: «текущее» сразу после «начал» — уже новое занятие (#65).
/// </summary>
public interface ITimelineRepository
{
    /// <summary>Отрезки, которые кончаются после <paramref name="fromUtc"/> (и открытый), обе дорожки.</summary>
    Task<Timeline> LoadAsync(Guid userId, DateTime fromUtc, CancellationToken cancellationToken);

    /// <summary>Отрезок пользователя по Id (любая дорожка, в том числе открытый).</summary>
    Task<Segment?> FindSegmentAsync(Guid userId, Guid segmentId, CancellationToken cancellationToken);

    Task<Segment?> FindOpenAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Сохраняет изменение (убирает <c>Before</c>, добавляет <c>After</c>) и пишет его в журнал.</summary>
    void Record(Guid userId, TimelineChange change, DateTime atUtc);

    /// <summary>
    /// Изменение, записанное последним, — порядок журнала это порядок вызовов <see cref="Record"/>, в том числе
    /// внутри одного сообщения, где у всех изменений одно и то же <c>atUtc</c>.
    /// </summary>
    Task<StoredTimelineChange?> LastChangeAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Откатывает изменение из журнала в хранилище и удаляет его из журнала.</summary>
    Task RevertAsync(Guid userId, StoredTimelineChange change, CancellationToken cancellationToken);
}
