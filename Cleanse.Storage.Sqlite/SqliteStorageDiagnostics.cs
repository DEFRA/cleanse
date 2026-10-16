using System.Diagnostics;
using Cleanse.Core.Abstractions;
using Cleanse.Storage.Sqlite.Schema;

namespace Cleanse.Storage.Sqlite;

/// <summary>
/// Proves the database by opening a connection and reading from it, rather than trusting that
/// the file exists. Returns a failure instead of throwing, because the caller is a status page.
/// </summary>
internal sealed class SqliteStorageDiagnostics(
    CleanseSqliteDatabase database,
    CleanseSqliteOptions options) : IStorageDiagnostics
{
    public string ProviderName => "Sqlite";

    public string? Target => options.InMemory ? "in-memory" : options.DataSource;

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM issues";
            var issues = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));

            return ProbeResult.Ok(Elapsed(started), $"{issues} issues");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProbeResult.Failed(Elapsed(started), ex.Message);
        }
    }

    private static double Elapsed(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;
}
