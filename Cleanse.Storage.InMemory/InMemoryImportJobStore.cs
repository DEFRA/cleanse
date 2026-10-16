using System.Collections.Concurrent;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Storage.InMemory;

public sealed class InMemoryImportJobStore : IImportJobStore
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Guid, ImportJobSnapshot> _jobs = new();
    private readonly List<ImportJobRejection> _rejections = [];

    public int Count => _jobs.Count;

    /// <summary>Hard delete, taking each job's rejections with it.</summary>
    public (int Jobs, int Rejections) Clear(string? groupId)
    {
        lock (_gate)
        {
            var doomed = groupId is null
                ? _jobs.Keys.ToList()
                : _jobs
                    .Where(e => string.Equals(e.Value.GroupId, groupId, StringComparison.Ordinal))
                    .Select(e => e.Key)
                    .ToList();

            foreach (var id in doomed)
            {
                _jobs.TryRemove(id, out _);
            }

            var removed = doomed.ToHashSet();
            return (doomed.Count, _rejections.RemoveAll(r => removed.Contains(r.ImportJobId)));
        }
    }

    public Task<ImportJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_jobs.TryGetValue(id, out var snapshot)
            ? ImportJob.FromSnapshot(snapshot)
            : null);
    }

    public Task<bool> TryCreateAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        // Stands in for the unique partial index the relational providers use.
        lock (_gate)
        {
            if (_jobs.Values.Any(j =>
                string.Equals(j.GroupId, job.GroupId, StringComparison.Ordinal) && IsActive(j.Status)))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_jobs.TryAdd(job.Id, job.ToSnapshot()));
        }
    }

    public Task SaveAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        _jobs[job.Id] = job.ToSnapshot();
        return Task.CompletedTask;
    }

    public Task<ImportJob?> GetActiveForGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        cancellationToken.ThrowIfCancellationRequested();

        var active = _jobs.Values
            .Where(j => string.Equals(j.GroupId, groupId, StringComparison.Ordinal) && IsActive(j.Status))
            .OrderByDescending(j => j.CreatedAtUtc)
            .FirstOrDefault();

        return Task.FromResult(active is null ? null : ImportJob.FromSnapshot(active));
    }

    public Task<ImportJob?> LeaseNextPendingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var next = _jobs.Values
                .Where(j => j.Status == ImportJobStatus.Pending)
                .OrderBy(j => j.CreatedAtUtc)
                .FirstOrDefault();

            if (next is null)
            {
                return Task.FromResult<ImportJob?>(null);
            }

            var job = ImportJob.FromSnapshot(next);
            if (job.Start(now).IsFailure)
            {
                return Task.FromResult<ImportJob?>(null);
            }

            _jobs[job.Id] = job.ToSnapshot();
            return Task.FromResult<ImportJob?>(job);
        }
    }

    public Task<IReadOnlyList<ImportJob>> FindStalledAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var stalled = _jobs.Values
            .Where(j => j.Status == ImportJobStatus.Processing &&
                        j.HeartbeatAtUtc is { } beat &&
                        beat < olderThan)
            .OrderBy(j => j.HeartbeatAtUtc)
            .Take(limit)
            .Select(ImportJob.FromSnapshot)
            .ToList();

        return Task.FromResult<IReadOnlyList<ImportJob>>(stalled);
    }

    public Task<PagedResult<ImportJob>> SearchAsync(
        string? groupId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        var matched = _jobs.Values
            .Where(j => groupId is null || string.Equals(j.GroupId, groupId, StringComparison.Ordinal))
            .ToList();

        var items = matched
            .OrderByDescending(j => j.CreatedAtUtc)
            .ThenByDescending(j => j.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(ImportJob.FromSnapshot)
            .ToList();

        return Task.FromResult(new PagedResult<ImportJob>(
            items, page.PageNumber, page.PageSize, matched.Count));
    }

    public Task AddRejectionsAsync(
        IReadOnlyCollection<ImportJobRejection> rejections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejections);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _rejections.AddRange(rejections);
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<ImportJobRejection>> GetRejectionsAsync(
        Guid importJobId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var matched = _rejections.Where(r => r.ImportJobId == importJobId).ToList();
            var items = matched
                .OrderBy(r => r.RowNumber)
                .Skip(page.Skip)
                .Take(page.PageSize)
                .ToList();

            return Task.FromResult(new PagedResult<ImportJobRejection>(
                items, page.PageNumber, page.PageSize, matched.Count));
        }
    }

    private static bool IsActive(ImportJobStatus status) =>
        status is ImportJobStatus.Pending or ImportJobStatus.Processing;
}

/// <summary>In-process file store, so tests and local spikes need no disk or bucket.</summary>
public sealed class InMemoryImportFileStore : IImportFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _files[key] = buffer.ToArray();
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_files.TryGetValue(key, out var bytes))
        {
            throw new FileNotFoundException($"No import file stored under '{key}'.", key);
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_files.ContainsKey(key));
    }
}
