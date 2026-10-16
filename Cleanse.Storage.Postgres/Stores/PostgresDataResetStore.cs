using Cleanse.Core.Abstractions;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresDataResetStore(CleanseDbContext db) : IDataResetStore
{
    public async Task<ResetCounts> ResetAsync(
        string? groupId,
        string actorId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        // DELETE rather than TRUNCATE: on CDP the running service connects as the DML-only role,
        // which is not granted TRUNCATE. The slower statement is the one that will actually run.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        async Task<int> DeleteAsync(string everything, string scopedToGroup) =>
            groupId is null
                ? await db.Database.ExecuteSqlRawAsync(everything, cancellationToken)
                : await db.Database.ExecuteSqlRawAsync(
                    scopedToGroup,
                    [new NpgsqlParameter("group", groupId)],
                    cancellationToken);

        var rejections = await DeleteAsync(
            $"DELETE FROM {Db.ImportJobRejections}",
            $"""
             DELETE FROM {Db.ImportJobRejections}
             WHERE import_job_id IN (SELECT id FROM {Db.ImportJobs} WHERE group_id = @group)
             """);

        var importJobs = await DeleteAsync(
            $"DELETE FROM {Db.ImportJobs}",
            $"DELETE FROM {Db.ImportJobs} WHERE group_id = @group");

        var history = await DeleteAsync(
            $"DELETE FROM {Db.IssueHistory}",
            $"""
             DELETE FROM {Db.IssueHistory}
             WHERE issue_id IN (SELECT {Db.Issue.Id} FROM {Db.Issues} WHERE {Db.Issue.GroupId} = @group)
             """);

        // Has to precede both tables it reads: audit rows reach their group through them.
        var audit = await DeleteAsync(
            $"DELETE FROM {Db.AuditEntries}",
            $"""
             DELETE FROM {Db.AuditEntries}
             WHERE issue_id IN (SELECT {Db.Issue.Id} FROM {Db.Issues} WHERE {Db.Issue.GroupId} = @group)
                OR run_id IN (SELECT run_id FROM {Db.RuleRuns} WHERE group_id = @group)
             """);

        var issues = await DeleteAsync(
            $"DELETE FROM {Db.Issues}",
            $"DELETE FROM {Db.Issues} WHERE {Db.Issue.GroupId} = @group");

        var runs = await DeleteAsync(
            $"DELETE FROM {Db.RuleRuns}",
            $"DELETE FROM {Db.RuleRuns} WHERE group_id = @group");

        db.DataResets.Add(new DataResetRow
        {
            AtUtc = at,
            Scope = groupId ?? "all",
            ActorId = actorId,
            Issues = issues
        });
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ResetCounts(issues, history, audit, runs, importJobs, rejections);
    }

    public async Task<DataResetRecord?> GetLastResetAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.DataResets.AsNoTracking()
            .OrderByDescending(r => r.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : new DataResetRecord(row.AtUtc, row.Scope, row.ActorId, row.Issues);
    }
}
