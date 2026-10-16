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
