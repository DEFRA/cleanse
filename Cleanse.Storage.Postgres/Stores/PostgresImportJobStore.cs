using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Imports;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresImportJobStore(CleanseDbContext db) : IImportJobStore
{
    public async Task<ImportJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> TryCreateAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        db.ImportJobs.Add(ToRow(job.ToSnapshot()));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The unique partial index rejected a second active job for this group.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task SaveAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var row = ToRow(job.ToSnapshot());
        var existing = await db.ImportJobs.FirstOrDefaultAsync(r => r.Id == row.Id, cancellationToken);

        if (existing is null)
        {
            db.ImportJobs.Add(row);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(row);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ImportJob?> GetActiveForGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var row = await db.ImportJobs.AsNoTracking()
            .Where(r => r.GroupId == groupId && (r.Status == "Pending" || r.Status == "Processing"))
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToDomain(row);
    }

    public async Task<ImportJob?> LeaseNextPendingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // FOR UPDATE SKIP LOCKED so two workers never claim the same job, and a worker is never
        // blocked waiting for one that is already being leased.
        var rows = await db.ImportJobs.FromSql($"""
            UPDATE import_jobs
            SET status = 'Processing', started_at_utc = {now}, heartbeat_at_utc = {now},
                version = version + 1
            WHERE id = (
                SELECT id FROM import_jobs
                WHERE status = 'Pending'
                ORDER BY created_at_utc ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);

        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<IReadOnlyList<ImportJob>> FindStalledAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await db.ImportJobs.AsNoTracking()
            .Where(r => r.Status == "Processing" &&
                        r.HeartbeatAtUtc != null &&
                        r.HeartbeatAtUtc < olderThan)
            .OrderBy(r => r.HeartbeatAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToDomain)];
    }

    public async Task<PagedResult<ImportJob>> SearchAsync(
        string? groupId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = db.ImportJobs.AsNoTracking();
        if (groupId is not null)
        {
            query = query.Where(r => r.GroupId == groupId);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ImportJob>(
            [.. rows.Select(ToDomain)], page.PageNumber, page.PageSize, total);
    }

    public async Task AddRejectionsAsync(
        IReadOnlyCollection<ImportJobRejection> rejections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejections);
        if (rejections.Count == 0)
        {
            return;
        }

        db.ImportJobRejections.AddRange(rejections.Select(r => new ImportJobRejectionRow
        {
            ImportJobId = r.ImportJobId,
            RowNumber = r.RowNumber,
            IssueId = r.IssueId,
            Reason = r.Reason,
            Detail = r.Detail
        }));

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<ImportJobRejection>> GetRejectionsAsync(
        Guid importJobId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = db.ImportJobRejections.AsNoTracking().Where(r => r.ImportJobId == importJobId);
        var total = await query.LongCountAsync(cancellationToken);

        var rows = await query
            .OrderBy(r => r.RowNumber)
            .ThenBy(r => r.Sequence)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ImportJobRejection>(
            [.. rows.Select(r => new ImportJobRejection(
                r.ImportJobId, r.RowNumber, r.IssueId, r.Reason, r.Detail))],
            page.PageNumber,
            page.PageSize,
            total);
    }

    private static ImportJob ToDomain(ImportJobRow row) => ImportJob.FromSnapshot(new ImportJobSnapshot(
        row.Id,
        row.GroupId,
        row.FileName,
        row.BlobKey,
        Enum.Parse<ImportJobStatus>(row.Status),
        new ImportJobCounters(
            row.TotalRows, row.ProcessedRows, row.Matched, row.Unmatched,
            row.DuplicatesCollapsed, row.Invalid),
        row.RequestedBy,
        row.CreatedAtUtc,
        row.StartedAtUtc,
        row.FinishedAtUtc,
        row.HeartbeatAtUtc,
        row.FailureReason,
        row.Version,
        row.TenantId,
        row.ClientId));

    private static ImportJobRow ToRow(ImportJobSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        TenantId = snapshot.TenantId,
        ClientId = snapshot.ClientId,
        GroupId = snapshot.GroupId,
        FileName = snapshot.FileName,
        BlobKey = snapshot.BlobKey,
        Status = snapshot.Status.ToString(),
        TotalRows = snapshot.Counters.TotalRows,
        ProcessedRows = snapshot.Counters.ProcessedRows,
        Matched = snapshot.Counters.Matched,
        Unmatched = snapshot.Counters.Unmatched,
        DuplicatesCollapsed = snapshot.Counters.DuplicatesCollapsed,
        Invalid = snapshot.Counters.Invalid,
        RequestedBy = snapshot.RequestedBy,
        CreatedAtUtc = snapshot.CreatedAtUtc,
        StartedAtUtc = snapshot.StartedAtUtc,
        FinishedAtUtc = snapshot.FinishedAtUtc,
        HeartbeatAtUtc = snapshot.HeartbeatAtUtc,
        FailureReason = snapshot.FailureReason,
        Version = snapshot.Version
    };
}
