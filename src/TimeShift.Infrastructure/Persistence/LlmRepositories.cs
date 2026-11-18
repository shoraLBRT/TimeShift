using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Llm;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Facts;
using TimeShift.Domain.Schedule;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class FactRepository(TimeShiftDbContext db) : IFactRepository
{
    public async Task<IReadOnlyDictionary<string, int>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.Facts.Where(f => f.UserId == userId && f.Minutes != null).LoadAsync(cancellationToken);

        // Local — загруженные и добавленные в этой же единице работы, без удалённых в ней.
        return db.Facts.Local
            .Where(f => f.UserId == userId && f.Minutes is not null)
            .ToDictionary(f => f.Key, f => f.Minutes!.Value);
    }

    public async Task<CategoryRules> RulesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.Facts.Where(f => f.UserId == userId && f.Value != null).LoadAsync(cancellationToken);
        return new CategoryRules(db.Facts.Local.Where(f => f.UserId == userId).Select(f => f.AsRule()).OfType<CategoryRule>());
    }

    public async Task SetRuleAsync(Guid userId, CategoryRule rule, CancellationToken cancellationToken)
    {
        var key = UserFact.RuleKey(rule.ActivityId, rule.Part);
        await db.Facts.Where(f => f.UserId == userId && f.Key == key).LoadAsync(cancellationToken);

        // Трекер знает и удалённое в этой единице работы: его возвращаем к жизни, а не добавляем второй раз.
        var tracked = db.ChangeTracker.Entries<UserFact>().FirstOrDefault(e => e.Entity.UserId == userId && e.Entity.Key == key);
        if (tracked is null)
        {
            db.Facts.Add(new UserFact(userId, rule));
            return;
        }

        if (tracked.State == EntityState.Deleted)
        {
            tracked.State = EntityState.Modified;
        }

        tracked.Entity.Value = rule.CategoryKey;
    }

    public async Task<bool> RemoveRuleAsync(Guid userId, Guid activityId, PartOfDay part, CancellationToken cancellationToken)
    {
        var key = UserFact.RuleKey(activityId, part);
        await db.Facts.Where(f => f.UserId == userId && f.Key == key).LoadAsync(cancellationToken);
        if (db.Facts.Local.FirstOrDefault(f => f.UserId == userId && f.Key == key) is not { } fact)
        {
            return false;
        }

        db.Facts.Remove(fact);
        return true;
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
