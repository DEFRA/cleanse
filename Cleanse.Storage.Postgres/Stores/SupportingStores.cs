using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresAuditStore(CleanseDbContext db) : IAuditStore
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        db.AuditEntries.Add(entry.ToRow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AppendBatchAsync(
        IReadOnlyCollection<AuditEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        db.AuditEntries.AddRange(entries.Select(e => e.ToRow()));
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<PagedResult<AuditEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        var id = issueId.Value;
        return PageAsync(db.AuditEntries.Where(r => r.IssueId == id), page, cancellationToken);
    }

    public Task<PagedResult<AuditEntry>> GetForRunAsync(
        string runId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return PageAsync(db.AuditEntries.Where(r => r.RunId == runId), page, cancellationToken);
    }

    private static async Task<PagedResult<AuditEntry>> PageAsync(
        IQueryable<AuditEntryRow> query,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        query = query.AsNoTracking();
        var total = await query.LongCountAsync(cancellationToken);

        // Sequence is the stable tie-break: a batch stamps every entry with the same instant.
        var rows = await query
            .OrderByDescending(r => r.AtUtc)
            .ThenByDescending(r => r.Sequence)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditEntry>(
            [.. rows.Select(r => r.ToDomain())], page.PageNumber, page.PageSize, total);
    }
}

internal sealed class PostgresIssueHistoryStore(CleanseDbContext db) : IIssueHistoryStore
{
    public async Task AddAsync(IssueHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var row = entry.ToSnapshot().ToRow();

        // Upsert rather than insert: imported entries carry deterministic ids so that re-running
        // an import converges instead of duplicating.
        var existing = await db.IssueHistory
            .FirstOrDefaultAsync(r => r.Id == row.Id, cancellationToken);

        if (existing is null)
        {
            db.IssueHistory.Add(row);
        }
        else
        {
            existing.ActionType = row.ActionType;
            existing.Comments = row.Comments;
            existing.CreatedAtUtc = row.CreatedAtUtc;
            existing.CreatedById = row.CreatedById;
            existing.CreatedByDisplayName = row.CreatedByDisplayName;
            existing.CreatedByKind = row.CreatedByKind;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IssueHistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.IssueHistory.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row?.ToDomain();
    }

    public async Task<PagedResult<IssueHistoryEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var id = issueId.Value;
        var query = db.IssueHistory.AsNoTracking().Where(r => r.IssueId == id);
        var total = await query.LongCountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Sequence)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<IssueHistoryEntry>(
            [.. rows.Select(r => r.ToDomain())], page.PageNumber, page.PageSize, total);
    }
}

internal sealed class PostgresRuleRunStore(CleanseDbContext db) : IRuleRunStore
{
    public async Task<RuleRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var row = await db.RuleRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == runId, cancellationToken);
        return row?.ToDomain();
    }

    public async Task<RuleRun?> GetActiveForGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var row = await db.RuleRuns.AsNoTracking()
            .Where(r => r.GroupId == groupId && r.State == RunningState)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return row?.ToDomain();
    }

    public async Task<RuleRun?> GetLatestForGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var row = await db.RuleRuns.AsNoTracking()
            .Where(r => r.GroupId == groupId)
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        return row?.ToDomain();
    }

    public async Task<PagedResult<RuleRun>> SearchAsync(
        string? groupId,
        IReadOnlyCollection<RunState>? states,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = db.RuleRuns.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            query = query.Where(r => r.GroupId == groupId);
        }

        if (states is { Count: > 0 })
        {
            var wanted = states.Select(s => s.ToString().ToLowerInvariant()).ToArray();
            query = query.Where(r => wanted.Contains(r.State));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.RunId)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<RuleRun>(
            [.. rows.Select(r => r.ToDomain())], page.PageNumber, page.PageSize, total);
    }

    public async Task<IReadOnlyList<RuleRun>> FindStalledAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await db.RuleRuns.AsNoTracking()
            .Where(r => r.State == RunningState &&
                        (r.HeartbeatAtUtc ?? r.StartedAtUtc) < olderThan)
            .OrderBy(r => r.HeartbeatAtUtc ?? r.StartedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task SaveAsync(RuleRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var row = run.ToSnapshot().ToRow();
        var existing = await db.RuleRuns.FirstOrDefaultAsync(r => r.RunId == row.RunId, cancellationToken);

        if (existing is null)
        {
            db.RuleRuns.Add(row);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(row);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static readonly string RunningState = RunState.Running.ToString().ToLowerInvariant();
}
