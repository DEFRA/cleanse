namespace Cleanse.Core.Abstractions;

/// <summary>
/// Hard deletes, as opposed to the end-of-run sweep which only marks issues inactive. Kept out
/// of the per-aggregate stores on purpose: a reset spans every table and has to be one
/// transaction, because a half-cleared database is worse than an untouched one.
/// </summary>
public interface IDataResetStore
{
    /// <summary>
    /// Permanently removes issues, their case history and audit trail, rule runs and import
    /// jobs. Confined to <paramref name="groupId"/> when one is supplied; otherwise everything.
    /// Records the reset itself, which is the one thing it must not delete.
    /// </summary>
    Task<ResetCounts> ResetAsync(
        string? groupId,
        string actorId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent reset, or null if none has been recorded. Deliberately survives a reset:
    /// "when was this wiped" is unanswerable if the answer is wiped along with everything else.
    /// </summary>
    Task<DataResetRecord?> GetLastResetAsync(CancellationToken cancellationToken = default);
}

public sealed record DataResetRecord(DateTimeOffset AtUtc, string Scope, string ActorId, int Issues);

public sealed record ResetCounts(
    int Issues,
    int IssueHistoryEntries,
    int AuditEntries,
    int RuleRuns,
    int ImportJobs,
    int ImportRejections);
