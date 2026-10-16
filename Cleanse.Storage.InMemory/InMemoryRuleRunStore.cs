using System.Collections.Concurrent;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

public sealed class InMemoryRuleRunStore : IRuleRunStore
{
    private readonly ConcurrentDictionary<string, RuleRunSnapshot> _runs = new(StringComparer.Ordinal);

    public int Count => _runs.Count;

    /// <summary>
    /// Hard delete. Returns the run ids removed so the audit log, which is keyed by run rather
    /// than by group, can be cleared to match.
    /// </summary>
    public IReadOnlyCollection<string> Clear(string? groupId)
    {
        var doomed = groupId is null
            ? _runs.Keys.ToList()
            : _runs
                .Where(e => string.Equals(e.Value.GroupId, groupId, StringComparison.Ordinal))
                .Select(e => e.Key)
                .ToList();

        foreach (var runId in doomed)
        {
            _runs.TryRemove(runId, out _);
        }

        return doomed;
    }

    public Task<RuleRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_runs.TryGetValue(runId, out var snapshot)
            ? RuleRun.FromSnapshot(snapshot)
            : null);
    }

    public Task<RuleRun?> GetActiveForGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        cancellationToken.ThrowIfCancellationRequested();

        var active = _runs.Values
            .Where(r => r.State == RunState.Running &&
                        string.Equals(r.GroupId, groupId, StringComparison.Ordinal))
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefault();

        return Task.FromResult(active is null ? null : RuleRun.FromSnapshot(active));
    }

    public Task<RuleRun?> GetLatestForGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        cancellationToken.ThrowIfCancellationRequested();

        var latest = _runs.Values
            .Where(r => string.Equals(r.GroupId, groupId, StringComparison.Ordinal))
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.RunId, StringComparer.Ordinal)
            .FirstOrDefault();

        return Task.FromResult(latest is null ? null : RuleRun.FromSnapshot(latest));
    }

    public Task<PagedResult<RuleRun>> SearchAsync(
        string? groupId,
        IReadOnlyCollection<RunState>? states,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = _runs.Values
            .Where(r => groupId is null || string.Equals(r.GroupId, groupId, StringComparison.Ordinal))
            .Where(r => states is null || states.Count == 0 || states.Contains(r.State))
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.RunId, StringComparer.Ordinal)
            .ToList();

        var items = matches
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(RuleRun.FromSnapshot)
            .ToList();

        return Task.FromResult(new PagedResult<RuleRun>(items, page.PageNumber, page.PageSize, matches.Count));
    }

    public Task<IReadOnlyList<RuleRun>> FindStalledAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RuleRun> stalled =
        [
            .. _runs.Values
                .Where(r => r.State == RunState.Running &&
                            (r.HeartbeatAtUtc ?? r.StartedAtUtc) < olderThan)
                .OrderBy(r => r.HeartbeatAtUtc ?? r.StartedAtUtc)
                .Take(limit)
                .Select(RuleRun.FromSnapshot)
        ];

        return Task.FromResult(stalled);
    }

    public Task SaveAsync(RuleRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();

        _runs[run.RunId] = run.ToSnapshot();
        return Task.CompletedTask;
    }
}
