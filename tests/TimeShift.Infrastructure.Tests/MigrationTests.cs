using Microsoft.EntityFrameworkCore;
using TimeShift.Infrastructure.Persistence;
using TimeShift.TestSupport;

namespace TimeShift.Infrastructure.Tests;

/// <summary>
/// Миграции ведутся отдельно для SQLite и Postgres (CLAUDE.md): модель не должна уходить вперёд ни от одного
/// набора, а набор Postgres обязан накатываться на пустую настоящую базу.
/// </summary>
public sealed class MigrationTests
{
    private const string NeedsPostgres = $"Нужен Postgres: задайте {TestDatabase.PostgresVariable}.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // База не нужна: снимок модели сравнивается с моделью в коде.
    [Fact]
    public void Sqlite_migrations_cover_the_model()
    {
        using var db = new SqliteTimeShiftDbContext(
            new DbContextOptionsBuilder<SqliteTimeShiftDbContext>().UseSqlite("Data Source=unused.db").Options);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Postgres_migrations_cover_the_model()
    {
        using var db = new PostgresTimeShiftDbContext(
            new DbContextOptionsBuilder<PostgresTimeShiftDbContext>().UseNpgsql("Host=unused").Options);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Postgres_migrations_apply_to_an_empty_database_and_keep_time_in_utc()
    {
        Assert.SkipUnless(TestDatabase.IsPostgres, NeedsPostgres);
        await using var database = TestDatabase.Create();
        await using var db = new PostgresTimeShiftDbContext(
            new DbContextOptionsBuilder<PostgresTimeShiftDbContext>().UseNpgsql(database.ConnectionString).Options);

        await db.Database.MigrateAsync(Ct);

        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(Ct));

        // Повторный запуск (каждый старт приложения) ничего не меняет и не падает.
        await db.Database.MigrateAsync(Ct);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));

        // Всё время — UTC: в Postgres это timestamptz, «timestamp without time zone» быть не должно.
        var timeColumnTypes = await db.Database
            .SqlQuery<string>($"SELECT DISTINCT data_type::text AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND data_type LIKE 'timestamp%'")
            .ToListAsync(Ct);
        Assert.Equal(["timestamp with time zone"], timeColumnTypes);
    }
}
