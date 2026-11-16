using TimeShift.Domain.Activities;

namespace TimeShift.Application.Abstractions;

public interface IActivityRepository
{
    Task<Activity?> GetAsync(Guid userId, Guid activityId, CancellationToken cancellationToken);

    /// <summary>Поиск по названию без учёта регистра и «ё».</summary>
    Task<Activity?> FindByNameAsync(Guid userId, string name, CancellationToken cancellationToken);

    /// <summary>Последние занятия по времени начала отрезков, без повторов.</summary>
    Task<IReadOnlyList<Activity>> RecentAsync(Guid userId, int count, CancellationToken cancellationToken);

    Task<IReadOnlyList<Activity>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Когда занятие в последний раз начиналось в ленте (по началу отрезков); занятия без отрезков не входят.</summary>
    Task<IReadOnlyDictionary<Guid, DateTime>> LastUsedAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Activity activity);
}
