using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite;

/// <summary>
/// SQLite is a development and test target only. Unlike Aurora on CDP there is no DDL
/// restriction, so this provider is allowed to create its own schema.
/// </summary>
public sealed class CleanseSqliteOptions
{
    /// <summary>
    /// A file path, or null for a private in-memory database. In-memory databases live only as
    /// long as a connection is held open, which <see cref="CleanseSqliteDatabase"/> handles.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>Shared-cache in-memory, so several connections see the same database.</summary>
    public bool InMemory => string.IsNullOrWhiteSpace(DataSource);

    /// <summary>Distinguishes concurrent in-memory databases within one process.</summary>
    public string InMemoryName { get; set; } = "cleanse";

    public int BusyTimeoutMilliseconds { get; set; } = 5_000;

    /// <summary>Rows per transaction on the batch path.</summary>
    public int BatchSize { get; set; } = 500;

    internal string BuildConnectionString() => InMemory
        ? new SqliteConnectionStringBuilder
        {
            DataSource = InMemoryName,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString()
        : new SqliteConnectionStringBuilder
        {
            DataSource = DataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true
        }.ToString();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BusyTimeoutMilliseconds, 0, nameof(BusyTimeoutMilliseconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1, nameof(BatchSize));

        if (InMemory && string.IsNullOrWhiteSpace(InMemoryName))
        {
            throw new InvalidOperationException($"{nameof(InMemoryName)} is required for an in-memory database.");
        }
    }
}
