using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TimeShift.Infrastructure.Persistence;

// Используются только инструментом dotnet-ef при генерации миграций; к базе не подключаются.

internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteTimeShiftDbContext>
{
    public SqliteTimeShiftDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteTimeShiftDbContext>()
            .UseSqlite("Data Source=timeshift.db")
            .Options);
}

internal sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresTimeShiftDbContext>
{
    public PostgresTimeShiftDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresTimeShiftDbContext>()
            .UseNpgsql("Host=localhost;Database=timeshift;Username=timeshift;Password=timeshift")
            .Options);
}
