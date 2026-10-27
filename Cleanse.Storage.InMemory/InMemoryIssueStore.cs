using System.Collections.Concurrent;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

/// <summary>
/// Reference implementation of <see cref="IIssueStore"/>. Everything is held as an immutable
/// <see cref="IssueSnapshot"/> and rehydrated on read, so callers get a fresh aggregate every
/// time and cannot mutate stored state by holding onto a reference. That mirrors how a real
/// database behaves and catches aliasing bugs that would otherwise only appear on Postgres.
/// </summary>
public sealed class InMemoryIssueStore : IIssueStore
{
    private readonly ConcurrentDictionary<string, IssueSnapshot> _issues = new(StringComparer.Ordinal);

    public int Count => _issues.Count;

    /// <summary>
    /// Hard delete. Returns the ids removed so the history and audit logs, which are keyed by
    /// issue rather than by group, can be cleared to match.
    /// </summary>
    public IReadOnlyCollection<string> Clear(string? groupId)
    {
        var doomed = groupId is null
            ? _issues.Keys.ToList()
            : _issues
                .Where(e => string.Equals(e.Value.GroupId, groupId, StringComparison.Ordinal))
                .Select(e => e.Key)
                .ToList();

        foreach (var id in doomed)
        {
            _issues.TryRemove(id, out _);
        }

        return doomed;
    }

    public Task<Issue?> GetAsync(IssueId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_issues.TryGetValue(id.Value, out var snapshot)
            ? Issue.FromSnapshot(snapshot)
            : null);
    }

    public Task<IReadOnlyList<Issue>> GetManyAsync(
        IReadOnlyCollection<IssueId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();

        var found = new List<Issue>(ids.Count);
        foreach (var id in ids)
        {
            if (_issues.TryGetValue(id.Value, out var snapshot))
            {
                found.Add(Issue.FromSnapshot(snapshot));
            }
        }

        return Task.FromResult<IReadOnlyList<Issue>>(found);
    }

    public Task<bool> SaveAsync(Issue issue, int? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = issue.ToSnapshot();
        if (expectedVersion is not { } expected)
        {
            _issues[snapshot.Id] = snapshot;
            return Task.FromResult(true);
        }

        if (!_issues.TryGetValue(snapshot.Id, out var current))
        {
            return Task.FromResult(_issues.TryAdd(snapshot.Id, snapshot));
        }

        if (current.Version != expected)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_issues.TryUpdate(snapshot.Id, snapshot, current));
    }

    public Task SaveBatchAsync(IReadOnlyCollection<Issue> issues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issues);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var issue in issues)
        {
            var snapshot = issue.ToSnapshot();
            _issues[snapshot.Id] = snapshot;
        }

        return Task.CompletedTask;
    }

    public Task<int> DeactivateStaleAsync(
        string groupId,
        string currentRunId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunId);
        cancellationToken.ThrowIfCancellationRequested();

        var deactivated = 0;
        foreach (var snapshot in _issues.Values)
        {
            if (snapshot.Status != IssueStatus.Active ||
                !string.Equals(snapshot.GroupId, groupId, StringComparison.Ordinal) ||
                string.Equals(snapshot.RunId, currentRunId, StringComparison.Ordinal))
            {
                continue;
            }

            var issue = Issue.FromSnapshot(snapshot);
            if (!issue.Deactivate(at).IsMaterial)
            {
                continue;
            }

            if (_issues.TryUpdate(snapshot.Id, issue.ToSnapshot(), snapshot))
            {
                deactivated++;
            }
        }

        return Task.FromResult(deactivated);
    }

    public Task<IReadOnlyList<Issue>> FindExpiredSuppressionsAsync(
        DateTimeOffset at,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = _issues.Values
            .Where(s => s.WorkflowStatus == WorkflowStatus.Suppressed &&
                        s.SuppressionExpiresAtUtc is { } expiry &&
                        expiry <= at)
            .OrderBy(s => s.SuppressionExpiresAtUtc)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(Issue.FromSnapshot)
            .ToList();

        return Task.FromResult<IReadOnlyList<Issue>>(matches);
    }

    public Task<PagedResult<Issue>> SearchAsync(IssueFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var matched = _issues.Values.Where(s => Matches(s, filter)).ToList();
        var total = matched.Count;

        var ordered = Sort(matched, filter)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .Select(Issue.FromSnapshot)
            .ToList();

        return Task.FromResult(new PagedResult<Issue>(
            ordered, filter.Page.PageNumber, filter.Page.PageSize, total));
    }

    public Task<IReadOnlyList<IssueStatusCount>> CountByStatusAsync(
        string? groupId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var counts = _issues.Values
            .Where(s => groupId is null || string.Equals(s.GroupId, groupId, StringComparison.Ordinal))
            .GroupBy(s => (s.Status, s.WorkflowStatus))
            .Select(g => new IssueStatusCount(g.Key.Status, g.Key.WorkflowStatus, g.Count()))
            .OrderBy(c => c.Status)
            .ThenBy(c => c.WorkflowStatus)
            .ToList();

        return Task.FromResult<IReadOnlyList<IssueStatusCount>>(counts);
    }

    private static IEnumerable<IssueSnapshot> Sort(List<IssueSnapshot> source, IssueFilter filter)
    {
        var descending = filter.SortDirection == SortDirection.Descending;

        // Id is the tie-break on every sort so paging stays stable across calls.
        return filter.SortBy switch
        {
            IssueSortField.CreatedAtUtc => Order(source, s => s.CreatedAtUtc, descending),
            IssueSortField.Id => Order(source, s => s.Id, descending),
            IssueSortField.RuleId => Order(source, s => s.RuleId, descending),
            IssueSortField.WorkflowStatus => Order(source, s => s.WorkflowStatus, descending),
            _ => Order(source, s => s.UpdatedAtUtc, descending)
        };
    }

    private static IEnumerable<IssueSnapshot> Order<TKey>(
        List<IssueSnapshot> source,
        Func<IssueSnapshot, TKey> selector,
        bool descending) =>
        descending
            ? source.OrderByDescending(selector).ThenBy(s => s.Id, StringComparer.Ordinal)
            : source.OrderBy(selector).ThenBy(s => s.Id, StringComparer.Ordinal);

    private static bool Matches(IssueSnapshot snapshot, IssueFilter filter)
    {
        if (filter.GroupId is { } group && !string.Equals(snapshot.GroupId, group, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.RuleId is { } rule && !string.Equals(snapshot.RuleId, rule, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.RunId is { } run && !string.Equals(snapshot.RunId, run, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.Status is { } status && snapshot.Status != status)
        {
            return false;
        }

        if (filter.WorkflowStatuses is { Count: > 0 } workflow && !workflow.Contains(snapshot.WorkflowStatus))
        {
            return false;
        }

        if (filter.AssignedTo is { } assignee &&
            !string.Equals(snapshot.AssignedTo, assignee, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.AssignedUserId is { } userId && snapshot.AssignedUserId != userId)
        {
            return false;
        }

        if (filter.Unassigned is { } unassigned &&
            (snapshot.AssignedUserId is null && string.IsNullOrWhiteSpace(snapshot.AssignedTo)) != unassigned)
        {
            return false;
        }

        if (filter.CreatedAfterUtc is { } createdAfter && snapshot.CreatedAtUtc < createdAfter)
        {
            return false;
        }

        if (filter.CreatedBeforeUtc is { } createdBefore && snapshot.CreatedAtUtc > createdBefore)
        {
            return false;
        }

        if (filter.UpdatedAfterUtc is { } updatedAfter && snapshot.UpdatedAtUtc < updatedAfter)
        {
            return false;
        }

        if (filter.UpdatedBeforeUtc is { } updatedBefore && snapshot.UpdatedAtUtc > updatedBefore)
        {
            return false;
        }

        if (filter.ContextMatches is { Count: > 0 } context)
        {
            foreach (var (key, value) in context)
            {
                if (!snapshot.Context.TryGetValue(key, out var actual) ||
                    !string.Equals(actual, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return filter.SearchTerm is not { } term ||
               string.IsNullOrWhiteSpace(term) ||
               MatchesSearchTerm(snapshot, term.Trim());
    }

    private static bool MatchesSearchTerm(IssueSnapshot snapshot, string term) =>
        snapshot.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        (snapshot.Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (snapshot.AssignedTo?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
        snapshot.Context.Values.Any(v => v.Contains(term, StringComparison.OrdinalIgnoreCase));
}
