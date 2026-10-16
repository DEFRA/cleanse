using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

/// <summary>Append-only in-memory audit log, ordered by insertion so reads are deterministic.</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _entries = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Hard delete. Entries are keyed by issue or by run, never by group, so a scoped clear has
    /// to be given the ids. Null for both sets wipes the whole log.
    /// </summary>
    public int Clear(IReadOnlyCollection<string>? issueIds, IReadOnlyCollection<string>? runIds)
    {
        lock (_gate)
        {
            if (issueIds is null && runIds is null)
            {
                var all = _entries.Count;
                _entries.Clear();
                return all;
            }

            var issues = issueIds?.ToHashSet(StringComparer.Ordinal) ?? [];
            var runs = runIds?.ToHashSet(StringComparer.Ordinal) ?? [];

            return _entries.RemoveAll(e =>
                (e.IssueId is { } issueId && issues.Contains(issueId.Value)) ||
                (e.RunId is { } runId && runs.Contains(runId)));
        }
    }

    public Task AppendBatchAsync(
        IReadOnlyCollection<AuditEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _entries.AddRange(entries);
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<AuditEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Query(e => e.IssueId is { } id && id.Value == issueId.Value, page, cancellationToken));

    public Task<PagedResult<AuditEntry>> GetForRunAsync(
        string runId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return Task.FromResult(Query(
            e => string.Equals(e.RunId, runId, StringComparison.Ordinal), page, cancellationToken));
    }

    private PagedResult<AuditEntry> Query(
        Func<AuditEntry, bool> predicate,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var matched = _entries
                .Select((entry, index) => (entry, index))
                .Where(x => predicate(x.entry))
                .ToList();

            // Newest first, tie-broken on insertion order. A batch writes many entries at the
            // same instant, so without a deterministic tie-break paging over them could repeat
            // or skip rows.
            var items = matched
                .OrderByDescending(x => x.entry.AtUtc)
                .ThenByDescending(x => x.index)
                .Select(x => x.entry)
                .Skip(page.Skip)
                .Take(page.PageSize)
                .ToList();

            return new PagedResult<AuditEntry>(items, page.PageNumber, page.PageSize, matched.Count);
        }
    }
}
