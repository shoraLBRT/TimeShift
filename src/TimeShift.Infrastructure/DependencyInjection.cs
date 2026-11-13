using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeShift.Application.Abstractions;
using TimeShift.Infrastructure.Persistence;

namespace TimeShift.Infrastructure;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует инфраструктуру. Провайдер БД — <c>Database:Provider</c> ("Sqlite" | "Postgres"),
    /// строка подключения — <c>ConnectionStrings:Default</c>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var providerName = configuration["Database:Provider"];
        if (!Enum.TryParse<DatabaseProvider>(providerName, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Unknown Database:Provider '{providerName}'. Expected 'Sqlite' or 'Postgres'.");
        }

        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
        }

        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                services.AddDbContext<TimeShiftDbContext, SqliteTimeShiftDbContext>(o => o.UseSqlite(connectionString));
                break;
            case DatabaseProvider.Postgres:
                services.AddDbContext<TimeShiftDbContext, PostgresTimeShiftDbContext>(o => o.UseNpgsql(connectionString));
                break;
        }

        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
