using Cleanse.Core.Domain;

namespace Cleanse.Core.Abstractions;

/// <summary>
/// The persistence port for issues. Deliberately coarse grained: the bulk operations exist so
/// a relational or document provider can satisfy them in a single round trip, while the merge
/// rules that decide what a re-detection may overwrite stay in the domain.
/// </summary>
public interface IIssueStore
{
    Task<Issue?> GetAsync(IssueId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Issue>> GetManyAsync(
        IReadOnlyCollection<IssueId> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimistic write. Returns false when the stored version no longer matches
    /// <paramref name="expectedVersion"/>, i.e. somebody else edited it first.
    /// </summary>
    Task<bool> SaveAsync(Issue issue, int? expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Last-write-wins bulk write used by the detection path.</summary>
    Task SaveBatchAsync(IReadOnlyCollection<Issue> issues, CancellationToken cancellationToken = default);

    /// <summary>
    /// The end-of-run sweep: every active issue in the group that the current run did not touch
    /// becomes inactive. Set based on purpose, returning only a count, so a large group does not
    /// cost one round trip or one audit row per issue.
    /// </summary>
    Task<int> DeactivateStaleAsync(
        string groupId,
        string currentRunId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Issue>> FindExpiredSuppressionsAsync(
        DateTimeOffset at,
        int limit,
        CancellationToken cancellationToken = default);

    Task<PagedResult<Issue>> SearchAsync(IssueFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Counts by status and workflow status. Spans every group when no group is named.</summary>
    Task<IReadOnlyList<IssueStatusCount>> CountByStatusAsync(
        string? groupId,
        CancellationToken cancellationToken = default);
}
