using Microsoft.Data.Sqlite;
using Npgsql;

namespace TimeShift.TestSupport;

/// <summary>
/// База на один тест. По умолчанию — временный файл SQLite. Если задана переменная окружения
/// <see cref="PostgresVariable"/> (строка подключения к серверу, где можно создавать базы), — отдельная
/// база на этом сервере; после теста она удаляется. Так одни и те же тесты идут на обоих провайдерах.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    public const string PostgresVariable = "TIMESHIFT_TEST_POSTGRES";

    private readonly string _name;

    private readonly string? _server;

    private TestDatabase(string provider, string connectionString, string name, string? server)
    {
        Provider = provider;
        ConnectionString = connectionString;
        _name = name;
        _server = server;
    }

    public static bool IsPostgres => Server is not null;

    /// <summary>Значение для <c>Database:Provider</c>.</summary>
    public string Provider { get; }

    /// <summary>Значение для <c>ConnectionStrings:Default</c>.</summary>
    public string ConnectionString { get; }

    private static string? Server =>
        Environment.GetEnvironmentVariable(PostgresVariable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    public static TestDatabase Create()
    {
        var name = $"timeshift_test_{Guid.NewGuid():N}";
        if (Server is not { } server)
        {
            return new TestDatabase("Sqlite", $"Data Source={Path.Combine(Path.GetTempPath(), name + ".db")}", name, null);
        }

        Execute(server, $"CREATE DATABASE \"{name}\"");

        // Без пула: у каждой базы своя строка подключения, и простаивающие соединения сотни баз
        // иначе упёрлись бы в max_connections сервера.
        var connectionString = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = false }.ConnectionString;
        return new TestDatabase("Postgres", connectionString, name, server);
    }

    public ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            Execute(_server, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
            return ValueTask.CompletedTask;
        }

        using (var connection = new SqliteConnection(ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var file in Directory.GetFiles(Path.GetTempPath(), _name + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Временный файл — не страшно.
            }
        }

        return ValueTask.CompletedTask;
    }

    private static void Execute(string server, string sql)
    {
        using var connection = new NpgsqlConnection(server);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
