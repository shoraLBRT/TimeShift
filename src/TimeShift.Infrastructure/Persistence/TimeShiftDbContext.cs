using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TimeShift.Application.Llm;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Facts;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence;

/// <summary>
/// Общий (не зависящий от провайдера) контекст. Миграции генерируются отдельно для каждого
/// провайдера через наследников: <see cref="SqliteTimeShiftDbContext"/> и <see cref="PostgresTimeShiftDbContext"/>.
/// </summary>
public abstract class TimeShiftDbContext : DbContext
{
    protected TimeShiftDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Segment> Segments => Set<Segment>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<TimelineChangeRecord> TimelineChanges => Set<TimelineChangeRecord>();

    public DbSet<Domain.Push.WebPushSubscription> PushSubscriptions => Set<Domain.Push.WebPushSubscription>();

    public DbSet<ServerKey> ServerKeys => Set<ServerKey>();

    public DbSet<UserFact> Facts => Set<UserFact>();

    public DbSet<PendingPeriod> PendingPeriods => Set<PendingPeriod>();

    public DbSet<MessageLogEntry> Messages => Set<MessageLogEntry>();

    public DbSet<Application.Questions.PendingQuestion> Questions => Set<Application.Questions.PendingQuestion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TimeShiftDbContext).Assembly);
    }

    /// <summary>Всё время — UTC (CLAUDE.md); SQLite не хранит Kind, поэтому при чтении помечаем явно.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}

public sealed class SqliteTimeShiftDbContext(DbContextOptions<SqliteTimeShiftDbContext> options)
    : TimeShiftDbContext(options);

public sealed class PostgresTimeShiftDbContext(DbContextOptions<PostgresTimeShiftDbContext> options)
    : TimeShiftDbContext(options);
