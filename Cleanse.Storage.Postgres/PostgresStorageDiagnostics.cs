using System.Diagnostics;
using Cleanse.Core.Abstractions;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;

namespace Cleanse.Storage.Postgres;

/// <summary>
/// Proves Aurora by actually querying it. A failure is returned rather than thrown, because the
/// caller is a status page whose entire job is to keep working when this does not.
/// </summary>
internal sealed class PostgresStorageDiagnostics(
    CleanseDbContext db,
    CleansePostgresOptions options) : IStorageDiagnostics
{
    public string ProviderName => "Postgres";

    /// <summary>Host, port and database only. The IAM token and username stay out of a web page.</summary>
    public string? Target => $"{options.Host}:{options.Port}/{options.Database}";

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var issues = await db.Issues.AsNoTracking().CountAsync(cancellationToken);
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
