using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Llm;
using TimeShift.Domain.Facts;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class FactRepository(TimeShiftDbContext db) : IFactRepository
{
    public async Task<IReadOnlyDictionary<string, int>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var stored = await db.Facts.Where(f => f.UserId == userId).ToListAsync(cancellationToken);
        var result = stored.ToDictionary(f => f.Key, f => f.Minutes);

        // Добавленные в этой же единице работы — тоже факты.
        foreach (var local in db.Facts.Local.Where(f => f.UserId == userId))
        {
            result[local.Key] = local.Minutes;
        }

        return result;
    }

    public async Task SetAsync(Guid userId, string key, int minutes, CancellationToken cancellationToken)
    {
        var existing = db.Facts.Local.FirstOrDefault(f => f.UserId == userId && f.Key == key)
            ?? await db.Facts.FirstOrDefaultAsync(f => f.UserId == userId && f.Key == key, cancellationToken);
        if (existing is null)
        {
            db.Facts.Add(new UserFact(userId, key, minutes));
        }
        else
        {
            existing.Minutes = minutes;
        }
    }
}

internal sealed class PendingPeriodRepository(TimeShiftDbContext db) : IPendingPeriodRepository
{
    public async Task<PendingPeriod?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Трекер знает и удалённые в этой единице работы (Local их не показывает, а запрос вернул бы их снова).
        if (Tracked(userId) is { } entry)
        {
            return entry.State == EntityState.Deleted ? null : entry.Entity;
        }

        return await db.PendingPeriods.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
    }

    public async Task SetAsync(PendingPeriod pending, CancellationToken cancellationToken)
    {
        await RemoveAsync(pending.UserId, cancellationToken);

        // Строка с тем же ключом уже есть в базе (удалена в этой единице работы) — заменить её новой: отцепить
        // удалённую и обновить строку. Иначе — просто добавить.
        if (Tracked(pending.UserId) is { } entry)
        {
            var existedInDatabase = entry.State == EntityState.Deleted;
            entry.State = EntityState.Detached;
            if (existedInDatabase)
            {
                db.PendingPeriods.Update(pending);
                return;
            }
        }

        db.PendingPeriods.Add(pending);
    }

    private Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<PendingPeriod>? Tracked(Guid userId) =>
        db.ChangeTracker.Entries<PendingPeriod>().FirstOrDefault(e => e.Entity.UserId == userId && e.State != EntityState.Detached);

    public async Task RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(userId, cancellationToken);
        if (existing is not null)
        {
            db.PendingPeriods.Remove(existing);
        }
    }
}

internal sealed class MessageLog(TimeShiftDbContext db) : IMessageLog
{
    public void Add(MessageLogEntry entry) => db.Messages.Add(entry);
}
