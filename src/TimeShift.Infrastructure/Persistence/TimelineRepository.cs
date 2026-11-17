using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Timelines;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class TimelineRepository(TimeShiftDbContext db) : ITimelineRepository
{
    /// <summary>Сколько изменений хранить для /undo на пользователя.</summary>
    private const int JournalDepth = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Timeline> LoadAsync(Guid userId, DateTime fromUtc, CancellationToken cancellationToken)
    {
        var segments = await ForUser(userId)
            .Where(s => s.EndUtc == null || s.EndUtc > fromUtc)
            .ToListAsync(cancellationToken);
        return new Timeline(segments);
    }

    public Task<Segment?> FindSegmentAsync(Guid userId, Guid segmentId, CancellationToken cancellationToken) =>
        ForUser(userId).FirstOrDefaultAsync(s => s.Id == segmentId, cancellationToken);

    public Task<Segment?> FindOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        ForUser(userId).FirstOrDefaultAsync(s => s.EndUtc == null && !s.IsParallel, cancellationToken);

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
            db.Segments.Add(segment);
            db.Entry(segment).Property("UserId").CurrentValue = userId;
        }

        db.TimelineChanges.Add(new TimelineChangeRecord
        {
            UserId = userId,
            AtUtc = atUtc,
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
        var record = db.TimelineChanges.Local
                .Where(c => c.UserId == userId && db.Entry(c).State != EntityState.Deleted)
                .MaxBy(c => c.AtUtc)
            ?? await db.TimelineChanges
                .Where(c => c.UserId == userId)
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
            db.Segments.Add(segment);
            db.Entry(segment).Property("UserId").CurrentValue = userId;
        }

        var record = await db.TimelineChanges.FindAsync([change.Id], cancellationToken);
        if (record is not null)
        {
            db.TimelineChanges.Remove(record);
        }
    }

    private IQueryable<Segment> ForUser(Guid userId) =>
        db.Segments.Where(s => EF.Property<Guid>(s, "UserId") == userId);

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
