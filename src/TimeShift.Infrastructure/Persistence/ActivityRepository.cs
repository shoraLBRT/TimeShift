using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Activities;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class ActivityRepository(TimeShiftDbContext db) : IActivityRepository
{
    public async Task<Activity?> GetAsync(Guid userId, Guid activityId, CancellationToken cancellationToken)
    {
        // FindAsync сначала смотрит в контекст — карточка, заведённая в этой же единице работы, ещё не в базе (#65).
        var activity = await db.Activities.FindAsync([activityId], cancellationToken);
        return activity?.UserId == userId ? activity : null;
    }

    public async Task<Activity?> FindByNameAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        var normalized = Activity.Normalize(name);

        // Сначала — добавленные в этой же единице работы (ещё не сохранены).
        return db.Activities.Local.FirstOrDefault(a => a.UserId == userId && a.NormalizedName == normalized)
            ?? await db.Activities.FirstOrDefaultAsync(
                a => a.UserId == userId && EF.Property<string>(a, nameof(Activity.NormalizedName)) == normalized,
                cancellationToken);
    }

    public async Task<IReadOnlyList<Activity>> RecentAsync(Guid userId, int count, CancellationToken cancellationToken)
    {
        var ids = await db.Segments
            .Where(s => EF.Property<Guid>(s, "UserId") == userId && s.ActivityId != null)
            .GroupBy(s => s.ActivityId)
            .Select(g => new { Id = g.Key!.Value, Last = g.Max(s => s.StartUtc) })
            .OrderByDescending(x => x.Last)
            .Take(count)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var activities = await db.Activities.Where(a => a.UserId == userId && ids.Contains(a.Id)).ToListAsync(cancellationToken);
        return ids.Select(id => activities.FirstOrDefault(a => a.Id == id)).OfType<Activity>().ToList();
    }

    public async Task<IReadOnlyList<Activity>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
    [
        .. await db.Activities.Where(a => a.UserId == userId).ToListAsync(cancellationToken),
        .. db.Activities.Local.Where(a => a.UserId == userId && db.Entry(a).State == EntityState.Added),
    ];

    public async Task<IReadOnlyDictionary<Guid, DateTime>> LastUsedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Segments
            .Where(s => EF.Property<Guid>(s, "UserId") == userId && s.ActivityId != null)
            .GroupBy(s => s.ActivityId)
            .Select(g => new { Id = g.Key!.Value, Last = g.Max(s => s.StartUtc) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => r.Last);
    }

    public void Add(Activity activity) => db.Activities.Add(activity);
}
