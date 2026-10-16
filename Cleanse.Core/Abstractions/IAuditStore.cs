using Cleanse.Core.Domain;

namespace Cleanse.Core.Abstractions;

/// <summary>Append-only system change log. Holds both issue-scoped and run-scoped entries.</summary>
public interface IAuditStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task AppendBatchAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken = default);

    Task<PagedResult<AuditEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default);

    Task<PagedResult<AuditEntry>> GetForRunAsync(
        string runId,
        PageRequest page,
        CancellationToken cancellationToken = default);
}

/// <summary>Append-only log of human case actions against an issue.</summary>
public interface IIssueHistoryStore
{
    Task AddAsync(IssueHistoryEntry entry, CancellationToken cancellationToken = default);

    Task<IssueHistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<IssueHistoryEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default);
}

public interface IRuleRunStore
{
    Task<RuleRun?> GetAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>The in-progress run for a group, if any. Enforces one concurrent run per group.</summary>
    Task<RuleRun?> GetActiveForGroupAsync(string groupId, CancellationToken cancellationToken = default);

    Task<RuleRun?> GetLatestForGroupAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Run history, newest first. Pass a null group to search across every group.</summary>
    Task<PagedResult<RuleRun>> SearchAsync(
        string? groupId,
        IReadOnlyCollection<RunState>? states,
        PageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Running runs whose heartbeat has gone quiet. Without this a process that died mid-run
    /// would leave its group permanently unable to start another.
    /// </summary>
    Task<IReadOnlyList<RuleRun>> FindStalledAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default);

    Task SaveAsync(RuleRun run, CancellationToken cancellationToken = default);
}
