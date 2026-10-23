using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Diagnostics;

/// <summary>
/// Everything an operator needs to answer "is this service healthy, and what is it sitting on".
/// </summary>
/// <remarks>
/// Lives in the host rather than Core deliberately. It inspects infrastructure rather than the
/// domain, and it needs <c>ILogger</c>, which Core does not take a dependency on.
/// </remarks>
public sealed record GetDiagnosticsQuery(string? GroupId) : IQuery<DiagnosticsDto>;

/// <summary>
/// Every probe is independent and every failure is contained: a status page that falls over when
/// a dependency is down is useless for diagnosing that dependency. Failures are logged at warning
/// so they reach OpenSearch, and reported as state rather than thrown.
/// </summary>
internal sealed class GetDiagnosticsQueryHandler(
    IIssueStore issues,
    IDataResetStore resets,
    IStorageDiagnostics database,
    ILogger<GetDiagnosticsQueryHandler> logger,
    IImportStorageDiagnostics? files = null,
    IKrdsDiagnostics? krds = null)
    : IQueryHandler<GetDiagnosticsQuery, DiagnosticsDto>
{
    public async Task<Result<DiagnosticsDto>> HandleAsync(
        GetDiagnosticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The remote probes share nothing with the database or each other, so they run while the
        // database is queried: a slow dependency then costs its own timeout once, not in sequence.
        var storageProbe = files is null
            ? Task.FromResult(ProbeResult.Unknown("Import storage is not registered in this host."))
            : ProbeAsync("storage", files.ProbeAsync, cancellationToken);

        var krdsProbe = krds is null
            ? Task.FromResult(ProbeResult.Unknown("The rules engine is not registered in this host."))
            : ProbeAsync("krds", krds.ProbeAsync, cancellationToken);

        var databaseProbe = await ProbeAsync("database", database.ProbeAsync, cancellationToken);
        var counts = await CountIssuesAsync(query.GroupId, cancellationToken);
        var lastReset = await LastResetAsync(cancellationToken);

        return new DiagnosticsDto(
            ToDto(database.ProviderName, database.Target, databaseProbe),
            ToDto(files?.ProviderName ?? "unknown", files?.Target, await storageProbe),
            ToDto(krds?.ProviderName ?? "unknown", krds?.Target, await krdsProbe),
            counts,
            lastReset);
    }

    private static DependencyDto ToDto(string provider, string? target, ProbeResult probe) =>
        new(provider, target, probe.State.ToWire(), probe.Detail, probe.ElapsedMilliseconds);

    private async Task<ProbeResult> ProbeAsync(
        string name,
        Func<CancellationToken, Task<ProbeResult>> probe,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await probe(cancellationToken);

            if (result.State == ProbeState.Failed)
            {
                logger.LogWarning("Diagnostics probe {Probe} reported a failure: {Detail}", name, result.Detail);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Diagnostics probe {Probe} threw", name);
            return ProbeResult.Failed(0, ex.Message);
        }
    }

    private async Task<IssueCountsDto> CountIssuesAsync(string? groupId, CancellationToken cancellationToken)
    {
        try
        {
            var counts = await issues.CountByStatusAsync(groupId, cancellationToken);

            return new IssueCountsDto(
                ProbeState.Ok.ToWire(),
                null,
                counts.Sum(c => c.Count),
                [.. counts.Select(c => new IssueStatusCountDto(
                    c.Status.ToWire(), c.WorkflowStatus.ToWire(), c.Count))]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Diagnostics could not count issues");
            return new IssueCountsDto(ProbeState.Unknown.ToWire(), ex.Message, 0, []);
        }
    }

    private async Task<DateTimeOffset?> LastResetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await resets.GetLastResetAsync(cancellationToken))?.AtUtc;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Diagnostics could not read the last reset");
            return null;
        }
    }
}
