using System.Collections.Concurrent;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

public sealed class InMemoryIssueHistoryStore : IIssueHistoryStore
{
    private readonly ConcurrentDictionary<Guid, (long Sequence, IssueHistorySnapshot Snapshot)> _entries = new();
    private long _sequence;

    public int Count => _entries.Count;

    /// <summary>Hard delete. A null <paramref name="issueIds"/> clears every entry.</summary>
    public int Clear(IReadOnlyCollection<string>? issueIds)
    {
        if (issueIds is null)
        {
            var all = _entries.Count;
            _entries.Clear();
            return all;
        }

        var doomed = issueIds.ToHashSet(StringComparer.Ordinal);
        var removed = 0;

        foreach (var (id, stored) in _entries)
        {
            if (doomed.Contains(stored.Snapshot.IssueId) && _entries.TryRemove(id, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    public Task AddAsync(IssueHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        _entries[entry.Id] = (Interlocked.Increment(ref _sequence), entry.ToSnapshot());
        return Task.CompletedTask;
    }

    public Task<IssueHistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.TryGetValue(id, out var stored)
            ? IssueHistoryEntry.FromSnapshot(stored.Snapshot)
            : null);
    }

    public Task<PagedResult<IssueHistoryEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        var matched = _entries.Values
            .Where(e => string.Equals(e.Snapshot.IssueId, issueId.Value, StringComparison.Ordinal))
            .ToList();

        // Insertion order breaks ties so entries logged in the same instant page consistently.
        var items = matched
            .OrderByDescending(e => e.Snapshot.CreatedAtUtc)
            .ThenByDescending(e => e.Sequence)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(e => IssueHistoryEntry.FromSnapshot(e.Snapshot))
            .ToList();

        return Task.FromResult(new PagedResult<IssueHistoryEntry>(
            items, page.PageNumber, page.PageSize, matched.Count));
    }
}
