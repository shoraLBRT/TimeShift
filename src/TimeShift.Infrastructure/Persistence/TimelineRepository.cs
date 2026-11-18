using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Timelines;

namespace TimeShift.Infrastructure.Persistence;

/// <summary>
/// Лента и журнал её изменений. Сохраняет вызывающий, один раз на единицу работы (сообщение, запрос), а читают ленту
/// и до сохранения — поэтому каждое чтение отвечает так, как ответила бы база после сохранения: к строкам из базы
/// добавляется записанное в этой единице работы и убирается убранное в ней (#65).
/// </summary>
internal sealed class TimelineRepository(TimeShiftDbContext db) : ITimelineRepository
{
    /// <summary>Сколько изменений хранить для /undo на пользователя.</summary>
    private const int JournalDepth = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Timeline> LoadAsync(Guid userId, DateTime fromUtc, CancellationToken cancellationToken) =>
        new(await SegmentsAsync(userId, s => s.EndUtc == null || s.EndUtc > fromUtc, cancellationToken));

    public async Task<Segment?> FindSegmentAsync(Guid userId, Guid segmentId, CancellationToken cancellationToken)
    {
        // FindAsync сначала смотрит в контекст: добавленный там уже есть, убранный — есть, но помечен удалённым.
        var segment = await db.Segments.FindAsync([segmentId], cancellationToken);
        return segment is not null && db.Entry(segment) is { State: not EntityState.Deleted } entry && UserOf(entry) == userId
            ? segment
            : null;
    }

    public async Task<Segment?> FindOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        (await SegmentsAsync(userId, s => s.EndUtc == null && !s.IsParallel, cancellationToken)).FirstOrDefault();

    public void Record(Guid userId, TimelineChange change, DateTime atUtc)
    {
        if (change.IsEmpty)
        {
            return;
        }

        foreach (var segment in change.Before)
        {
            db.Segments.Remove(Tracked(segment));
        }

        foreach (var segment in change.After)
        {
            Put(userId, segment);
        }

        db.TimelineChanges.Add(new TimelineChangeRecord
        {
            UserId = userId,
            AtUtc = NextJournalTime(userId, atUtc),
            BeforeJson = JsonSerializer.Serialize(change.Before.Select(SegmentDto.From), Json),
            AfterJson = JsonSerializer.Serialize(change.After.Select(SegmentDto.From), Json),
        });

        // Старые записи журнала больше не нужны.
        var stale = db.TimelineChanges
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.AtUtc)
            .Skip(JournalDepth);
        db.TimelineChanges.RemoveRange(stale);
    }

    public async Task<StoredTimelineChange?> LastChangeAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Записанное в этой единице работы позже всего, что есть в базе (NextJournalTime); откатанное в ней — не в счёт.
        var pending = db.ChangeTracker.Entries<TimelineChangeRecord>().Where(e => e.Entity.UserId == userId).ToList();
        var reverted = pending.Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id).ToList();
        var record = pending.Where(e => e.State == EntityState.Added).Select(e => e.Entity).MaxBy(c => c.AtUtc)
            ?? await db.TimelineChanges
                .Where(c => c.UserId == userId && !reverted.Contains(c.Id))
                .OrderByDescending(c => c.AtUtc)
                .FirstOrDefaultAsync(cancellationToken);
        if (record is null)
        {
            return null;
        }

        var before = Deserialize(record.BeforeJson);
        var after = Deserialize(record.AfterJson);
        return new StoredTimelineChange(record.Id, record.AtUtc, new TimelineChange(before, after));
    }

    public async Task RevertAsync(Guid userId, StoredTimelineChange change, CancellationToken cancellationToken)
    {
        foreach (var segment in change.Change.After)
        {
            var stored = await db.Segments.FindAsync([segment.Id], cancellationToken);
            if (stored is not null)
            {
                db.Segments.Remove(stored);
            }
        }

        foreach (var segment in change.Change.Before)
        {
            Put(userId, segment);
        }

        var record = await db.TimelineChanges.FindAsync([change.Id], cancellationToken);
        if (record is not null)
        {
            db.TimelineChanges.Remove(record);
        }
    }

    /// <summary>
    /// Отрезки пользователя под условие — с учётом несохранённого. Запрос подтягивает подходящие строки базы в контекст;
    /// убранные в этой единице работы среди них остаются (они ещё в базе), добавленных в ней нет вовсе — поэтому
    /// ответ собирается из контекста тем же условием, а не из результата запроса.
    /// </summary>
    private async Task<List<Segment>> SegmentsAsync(Guid userId, Expression<Func<Segment, bool>> filter, CancellationToken cancellationToken)
    {
        await ForUser(userId).Where(filter).LoadAsync(cancellationToken);
        var matches = filter.Compile();
        return db.ChangeTracker.Entries<Segment>()
            .Where(e => e.State is not (EntityState.Deleted or EntityState.Detached) && UserOf(e) == userId && matches(e.Entity))
            .Select(e => e.Entity)
            .ToList();
    }

    private IQueryable<Segment> ForUser(Guid userId) =>
        db.Segments.Where(s => EF.Property<Guid>(s, "UserId") == userId);

    private static Guid UserOf(EntityEntry<Segment> entry) => entry.Property<Guid>("UserId").CurrentValue;

    /// <summary>
    /// Кладёт отрезок в ленту. Если строка с этим Id ещё в базе и убрана в этой же единице работы (откат несохранённого
    /// изменения), удаление просто отменяется: отрезки неизменяемы, строка та же самая.
    /// </summary>
    private void Put(Guid userId, Segment segment)
    {
        if (db.ChangeTracker.Entries<Segment>().FirstOrDefault(e => e.Entity.Id == segment.Id) is { State: EntityState.Deleted } removed)
        {
            removed.State = EntityState.Unchanged;
            return;
        }

        db.Segments.Add(segment);
        db.Entry(segment).Property("UserId").CurrentValue = userId;
    }

    /// <summary>
    /// Время записи журнала — оно же порядок <c>/undo</c>, поэтому у одного пользователя оно строго растёт: изменения
    /// одного сообщения приходят с одним и тем же «сейчас», и каждое следующее сдвигается на микросекунду после
    /// предыдущего (микросекунда — точность <c>timestamptz</c> в Postgres; SQLite хранит точнее).
    /// </summary>
    private DateTime NextJournalTime(Guid userId, DateTime atUtc)
    {
        var at = new DateTime(atUtc.Ticks - (atUtc.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
        var pending = db.ChangeTracker.Entries<TimelineChangeRecord>()
            .Where(e => e.State == EntityState.Added && e.Entity.UserId == userId)
            .Select(e => (DateTime?)e.Entity.AtUtc)
            .Max();
        var last = pending ?? db.TimelineChanges.Where(c => c.UserId == userId).Max(c => (DateTime?)c.AtUtc);
        return last is { } latest && at <= latest ? latest.AddMicroseconds(1) : at;
    }

    /// <summary>Экземпляр из контекста с тем же Id (отрезки неизменяемы — сравниваем по ключу).</summary>
    private Segment Tracked(Segment segment) =>
        db.Segments.Local.FirstOrDefault(s => s.Id == segment.Id) ?? segment;

    private static List<Segment> Deserialize(string json) =>
        (JsonSerializer.Deserialize<List<SegmentDto>>(json, Json) ?? []).Select(d => d.ToSegment()).ToList();

    private sealed record SegmentDto(
        Guid Id,
        DateTime StartUtc,
        DateTime? EndUtc,
        string Title,
        SegmentStatus Status,
        SegmentSource Source,
        Guid? ActivityId,
        string? CategoryKey,
        TimeSpan ElapsedBefore,
        bool IsParallel)
    {
        public static SegmentDto From(Segment s) =>
            new(s.Id, s.StartUtc, s.EndUtc, s.Title, s.Status, s.Source, s.ActivityId, s.CategoryKey, s.ElapsedBefore, s.IsParallel);

        public Segment ToSegment() => new(
            DateTime.SpecifyKind(StartUtc, DateTimeKind.Utc),
            EndUtc is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : null,
            Title, Status, Source, ActivityId, CategoryKey, ElapsedBefore, IsParallel, Id);
    }
}
