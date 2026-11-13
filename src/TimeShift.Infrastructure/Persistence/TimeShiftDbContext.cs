using Microsoft.EntityFrameworkCore;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TimeShiftDbContext).Assembly);
    }
}

public sealed class SqliteTimeShiftDbContext(DbContextOptions<SqliteTimeShiftDbContext> options)
    : TimeShiftDbContext(options);

public sealed class PostgresTimeShiftDbContext(DbContextOptions<PostgresTimeShiftDbContext> options)
    : TimeShiftDbContext(options);
