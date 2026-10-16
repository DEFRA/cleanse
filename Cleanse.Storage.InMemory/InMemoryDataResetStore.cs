using Cleanse.Core.Abstractions;

namespace Cleanse.Storage.InMemory;

/// <summary>
/// Clears the in-memory stores as a set. There is no transaction to join, so the one guarantee
/// on offer is that each store is cleared under its own lock; a concurrent writer could still
/// land a row immediately afterwards. Acceptable for a provider that exists for tests and local
/// spikes, and the relational providers do this properly.
/// </summary>
public sealed class InMemoryDataResetStore(
    InMemoryIssueStore issues,
    InMemoryIssueHistoryStore history,
    InMemoryAuditStore audit,
    InMemoryRuleRunStore runs,
    InMemoryImportJobStore imports) : IDataResetStore
{
    private DataResetRecord? _lastReset;

    public Task<ResetCounts> ResetAsync(
        string? groupId,
        string actorId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Issues and runs first: they yield the ids the keyless logs are filtered on.
        var issueIds = issues.Clear(groupId);
        var runIds = runs.Clear(groupId);

        var historyRemoved = history.Clear(groupId is null ? null : issueIds);
        var auditRemoved = audit.Clear(
            groupId is null ? null : issueIds,
            groupId is null ? null : runIds);
        var (jobs, rejections) = imports.Clear(groupId);

        _lastReset = new DataResetRecord(at, groupId ?? "all", actorId, issueIds.Count);

        return Task.FromResult(new ResetCounts(
            issueIds.Count, historyRemoved, auditRemoved, runIds.Count, jobs, rejections));
    }

    public Task<DataResetRecord?> GetLastResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_lastReset);
    }
}
