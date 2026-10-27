using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Schema;

/// <summary>
/// Owns the database lifetime and hands out configured connections. For an in-memory database it
/// keeps one connection open for as long as it lives, because SQLite discards an in-memory
/// database the moment the last connection to it closes.
/// </summary>
public sealed class CleanseSqliteDatabase : IAsyncDisposable
{
    private readonly CleanseSqliteOptions _options;
    private readonly string _connectionString;
    private SqliteConnection? _keepAlive;

    public CleanseSqliteDatabase(CleanseSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _connectionString = options.BuildConnectionString();
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ApplyPragmasAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>Creates the schema if it is not already there. Safe to call repeatedly.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        if (_options.InMemory && _keepAlive is null)
        {
            _keepAlive = new SqliteConnection(_connectionString);
            await _keepAlive.OpenAsync(cancellationToken);
            await ApplyPragmasAsync(_keepAlive, cancellationToken);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ReadSchemaScript();
        await command.ExecuteNonQueryAsync(cancellationToken);
        await UpgradeUserAssignmentAsync(connection, cancellationToken);
        await UpgradeImportScopeAsync(connection, cancellationToken);
    }

    private static async Task UpgradeImportScopeAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(import_jobs)";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "tenant_id", "client_id" })
        {
            if (!columns.Contains(column))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE import_jobs ADD COLUMN {column} TEXT NULL";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task UpgradeUserAssignmentAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Existing local databases predate stable-user assignment. CREATE TABLE IF NOT EXISTS
        // deliberately leaves those tables alone, so add only the two new nullable columns.
        await using var transaction = connection.BeginTransaction();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "PRAGMA table_info(issues)";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, definition) in new[]
        {
            ("assigned_user_id", "TEXT NULL REFERENCES users(id) ON DELETE RESTRICT"),
            ("assigned_user_full_name", "TEXT NULL")
        })
        {
            if (!columns.Contains(name))
            {
                await using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandText = $"ALTER TABLE issues ADD COLUMN {name} {definition}";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // Foreign keys are off by default in SQLite and are a per-connection setting, so without
        // this the issue_history constraint silently does nothing.
        command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};";

        if (!_options.InMemory)
        {
            // WAL lets readers run alongside the single writer. Meaningless for in-memory.
            command.CommandText += " PRAGMA journal_mode = WAL;";
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadSchemaScript()
    {
        const string resource = "Cleanse.Storage.Sqlite.Schema.schema.sql";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded schema '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
            _keepAlive = null;
        }
    }
}
