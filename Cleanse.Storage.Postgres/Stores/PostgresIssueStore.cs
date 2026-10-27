using System.Data;
using System.Text;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Cleanse.Storage.Postgres.Stores;

/// <summary>
/// Reads go through EF. The batch upsert, the sweep and the dynamic search drop to SQL: the
/// first two are set operations EF would turn into per-row round trips, and the third needs
/// exact control over casts and index usage.
/// </summary>
internal sealed class PostgresIssueStore(CleanseDbContext db, CleansePostgresOptions options) : IIssueStore
{
    private const string UpsertAssignments = $"""
            {Db.Issue.GroupId} = EXCLUDED.{Db.Issue.GroupId},
            {Db.Issue.RuleId} = EXCLUDED.{Db.Issue.RuleId},
            {Db.Issue.RunId} = EXCLUDED.{Db.Issue.RunId},
            {Db.Issue.Status} = EXCLUDED.{Db.Issue.Status},
            {Db.Issue.WorkflowStatus} = EXCLUDED.{Db.Issue.WorkflowStatus},
            {Db.Issue.AssignedTo} = EXCLUDED.{Db.Issue.AssignedTo},
            {Db.Issue.AssignedUserId} = EXCLUDED.{Db.Issue.AssignedUserId},
            {Db.Issue.AssignedUserFullName} = EXCLUDED.{Db.Issue.AssignedUserFullName},
            {Db.Issue.Notes} = EXCLUDED.{Db.Issue.Notes},
            {Db.Issue.ExternalNotes} = EXCLUDED.{Db.Issue.ExternalNotes},
            {Db.Issue.Priority} = EXCLUDED.{Db.Issue.Priority},
            {Db.Issue.ExternalReceivedAtUtc} = EXCLUDED.{Db.Issue.ExternalReceivedAtUtc},
            {Db.Issue.SuppressionExpiresAtUtc} = EXCLUDED.{Db.Issue.SuppressionExpiresAtUtc},
            {Db.Issue.Context} = EXCLUDED.{Db.Issue.Context},
            {Db.Issue.UpdatedAtUtc} = EXCLUDED.{Db.Issue.UpdatedAtUtc},
            {Db.Issue.ActivatedAtUtc} = EXCLUDED.{Db.Issue.ActivatedAtUtc},
            {Db.Issue.DeactivatedAtUtc} = EXCLUDED.{Db.Issue.DeactivatedAtUtc},
            {Db.Issue.Version} = EXCLUDED.{Db.Issue.Version}
        """;

    public async Task<Issue?> GetAsync(IssueId id, CancellationToken cancellationToken = default)
    {
        var row = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id.Value, cancellationToken);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Issue>> GetManyAsync(
        IReadOnlyCollection<IssueId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var keys = ids.Select(i => i.Value).ToArray();
        var rows = await db.Issues.AsNoTracking()
            .Where(r => keys.Contains(r.Id))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<bool> SaveAsync(
        Issue issue,
        int? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var row = issue.ToSnapshot().ToRow();

        // Insert-or-update in one statement. With a version supplied the DO UPDATE is filtered,
        // so a stale caller affects zero rows and learns it lost the race.
        var guard = expectedVersion is null
            ? string.Empty
            : $"WHERE {Db.Issues}.{Db.Issue.Version} = @expected_version";

        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Db.Issues} ({Db.Issue.AllColumns})
            VALUES {IssueValuesClause()}
            ON CONFLICT ({Db.Issue.Id}) DO UPDATE SET
            {UpsertAssignments}
            {guard}
            """, connection);

        AddIssueParameters(command, row);
        if (expectedVersion is { } expected)
        {
            command.Parameters.AddWithValue("expected_version", expected);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task SaveBatchAsync(
        IReadOnlyCollection<Issue> issues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count == 0)
        {
            return;
        }

        var rows = issues.Select(i => i.ToSnapshot().ToRow()).ToList();
        var connection = await OpenConnectionAsync(cancellationToken);

        if (rows.Count <= options.CopyThreshold)
        {
            await UpsertByValuesAsync(connection, rows, cancellationToken);
            return;
        }

        await UpsertByCopyAsync(connection, rows, cancellationToken);
    }

    /// <summary>Small batches: one multi-row INSERT beats the cost of staging a temp table.</summary>
    private static async Task UpsertByValuesAsync(
        NpgsqlConnection connection,
        List<IssueRow> rows,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder($"INSERT INTO {Db.Issues} ({Db.Issue.AllColumns}) VALUES").AppendLine();
        await using var command = new NpgsqlCommand(string.Empty, connection);

        for (var i = 0; i < rows.Count; i++)
        {
            sql.Append(i > 0 ? ",\n" : string.Empty)
                .Append(IssueValuesClause(i.ToString()));
            AddIssueParameters(command, rows[i], i.ToString());
        }

        sql.AppendLine()
            .AppendLine($"ON CONFLICT ({Db.Issue.Id}) DO UPDATE SET")
            .Append(UpsertAssignments);

        command.CommandText = sql.ToString();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Large batches: binary COPY into a staging table, then one set-based merge. This is the
    /// nightly rules-engine path, where row-at-a-time would be the bottleneck. The explicit
    /// transaction is what makes ON COMMIT DROP meaningful and the merge atomic.
    /// </summary>
    private static async Task UpsertByCopyAsync(
        NpgsqlConnection connection,
        List<IssueRow> rows,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string staging = "issues_stage";

        await using (var create = new NpgsqlCommand(
            $"CREATE TEMP TABLE {staging} (LIKE {Db.Issues}) ON COMMIT DROP", connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            $"COPY {staging} ({Db.Issue.AllColumns}) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var row in rows)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(row.Id, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.GroupId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.RuleId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.RunId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.Status, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.WorkflowStatus, NpgsqlDbType.Text, cancellationToken);
                await WriteNullableTextAsync(writer, row.AssignedTo, cancellationToken);
                await WriteNullableTextAsync(writer, row.Notes, cancellationToken);
                await WriteNullableTextAsync(writer, row.ExternalNotes, cancellationToken);
                await WriteNullableTextAsync(writer, row.Priority, cancellationToken);
                await WriteNullableTimestampAsync(writer, row.ExternalReceivedAtUtc, cancellationToken);
                await WriteNullableTimestampAsync(writer, row.SuppressionExpiresAtUtc, cancellationToken);
                await writer.WriteAsync(row.Context, NpgsqlDbType.Jsonb, cancellationToken);
                await writer.WriteAsync(row.CreatedAtUtc, NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(row.UpdatedAtUtc, NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(row.ActivatedAtUtc, NpgsqlDbType.TimestampTz, cancellationToken);
                await WriteNullableTimestampAsync(writer, row.DeactivatedAtUtc, cancellationToken);
                await writer.WriteAsync(row.Version, NpgsqlDbType.Integer, cancellationToken);
                if (row.AssignedUserId is { } userId)
                {
                    await writer.WriteAsync(userId, NpgsqlDbType.Uuid, cancellationToken);
                }
                else
                {
                    await writer.WriteNullAsync(cancellationToken);
                }
                await WriteNullableTextAsync(writer, row.AssignedUserFullName, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using (var merge = new NpgsqlCommand($"""
            INSERT INTO {Db.Issues} ({Db.Issue.AllColumns})
            SELECT {Db.Issue.AllColumns} FROM {staging}
            ON CONFLICT ({Db.Issue.Id}) DO UPDATE SET
            {UpsertAssignments}
            """, connection, transaction))
        {
            await merge.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> DeactivateStaleAsync(
        string groupId,
        string currentRunId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunId);

        // Mirrors Issue.Deactivate exactly: status, both timestamps and version move together.
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Db.Issues}
            SET {Db.Issue.Status} = @inactive,
                {Db.Issue.UpdatedAtUtc} = @at,
                {Db.Issue.DeactivatedAtUtc} = @at,
                {Db.Issue.Version} = {Db.Issue.Version} + 1
            WHERE {Db.Issue.GroupId} = @group_id
              AND {Db.Issue.Status} = @active
              AND {Db.Issue.RunId} <> @run_id
            """, connection);

        command.Parameters.AddWithValue("inactive", StatusNames.Inactive);
        command.Parameters.AddWithValue("active", StatusNames.Active);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
        command.Parameters.AddWithValue("group_id", groupId);
        command.Parameters.AddWithValue("run_id", currentRunId);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Issue>> FindExpiredSuppressionsAsync(
        DateTimeOffset at,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var suppressed = StatusNames.Suppressed;
        var rows = await db.Issues.AsNoTracking()
            .Where(r => r.WorkflowStatus == suppressed &&
                        r.SuppressionExpiresAtUtc != null &&
                        r.SuppressionExpiresAtUtc <= at)
            .OrderBy(r => r.SuppressionExpiresAtUtc)
            .ThenBy(r => r.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<PagedResult<Issue>> SearchAsync(
        IssueFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var where = IssueFilterSql.Build(filter);
        var connection = await OpenConnectionAsync(cancellationToken);

        await using var countCommand = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {Db.Issues} {where.Sql}", connection);
        where.AddParameters(countCommand);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        if (total == 0)
        {
            return PagedResult<Issue>.Empty(filter.Page);
        }

        await using var pageCommand = new NpgsqlCommand($"""
            SELECT {Db.Issue.AllColumns} FROM {Db.Issues}
            {where.Sql}
            ORDER BY {IssueFilterSql.OrderBy(filter)}
            LIMIT @limit OFFSET @offset
            """, connection);
        where.AddParameters(pageCommand);
        pageCommand.Parameters.AddWithValue("limit", filter.Page.PageSize);
        pageCommand.Parameters.AddWithValue("offset", filter.Page.Skip);

        var items = new List<Issue>(filter.Page.PageSize);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadIssue(reader));
        }

        return new PagedResult<Issue>(items, filter.Page.PageNumber, filter.Page.PageSize, total);
    }

    public async Task<IReadOnlyList<IssueStatusCount>> CountByStatusAsync(
        string? groupId,
        CancellationToken cancellationToken = default)
    {
        var grouped = await db.Issues.AsNoTracking()
            .Where(r => groupId == null || r.GroupId == groupId)
            .GroupBy(r => new { r.Status, r.WorkflowStatus })
            .Select(g => new { g.Key.Status, g.Key.WorkflowStatus, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return
        [
            .. grouped
                .Select(g => new IssueStatusCount(
                    RowMappings.ParseIssueStatus(g.Status),
                    RowMappings.ParseWorkflowStatus(g.WorkflowStatus),
                    g.Count))
                .OrderBy(c => c.Status)
                .ThenBy(c => c.WorkflowStatus)
        ];
    }

    private static Issue ReadIssue(NpgsqlDataReader reader) => Issue.FromSnapshot(new IssueSnapshot(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        RowMappings.ParseIssueStatus(reader.GetString(4)),
        RowMappings.ParseWorkflowStatus(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : RowMappings.ParsePriority(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        RowMappings.DeserialiseContext(reader.GetString(12)),
        reader.GetFieldValue<DateTimeOffset>(13),
        reader.GetFieldValue<DateTimeOffset>(14),
        reader.GetFieldValue<DateTimeOffset>(15),
        reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
        reader.GetInt32(17),
        reader.IsDBNull(18) ? null : reader.GetGuid(18),
        reader.IsDBNull(19) ? null : reader.GetString(19)));

    internal static void AddIssueParameters(NpgsqlCommand command, IssueRow row, string suffix = "")
    {
        var names = IssueParameterNames(suffix);

        command.Parameters.AddWithValue(names[0], row.Id);
        command.Parameters.AddWithValue(names[1], row.GroupId);
        command.Parameters.AddWithValue(names[2], row.RuleId);
        command.Parameters.AddWithValue(names[3], row.RunId);
        command.Parameters.AddWithValue(names[4], row.Status);
        command.Parameters.AddWithValue(names[5], row.WorkflowStatus);
        command.Parameters.AddWithValue(names[6], (object?)row.AssignedTo ?? DBNull.Value);
        command.Parameters.AddWithValue(names[7], (object?)row.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue(names[8], (object?)row.ExternalNotes ?? DBNull.Value);
        command.Parameters.AddWithValue(names[9], (object?)row.Priority ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter(names[10], NpgsqlDbType.TimestampTz)
        {
            Value = (object?)row.ExternalReceivedAtUtc ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter(names[11], NpgsqlDbType.TimestampTz)
        {
            Value = (object?)row.SuppressionExpiresAtUtc ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter(names[12], NpgsqlDbType.Jsonb) { Value = row.Context });
        command.Parameters.Add(new NpgsqlParameter(names[13], NpgsqlDbType.TimestampTz) { Value = row.CreatedAtUtc });
        command.Parameters.Add(new NpgsqlParameter(names[14], NpgsqlDbType.TimestampTz) { Value = row.UpdatedAtUtc });
        command.Parameters.Add(new NpgsqlParameter(names[15], NpgsqlDbType.TimestampTz) { Value = row.ActivatedAtUtc });
        command.Parameters.Add(new NpgsqlParameter(names[16], NpgsqlDbType.TimestampTz)
        {
            Value = (object?)row.DeactivatedAtUtc ?? DBNull.Value
        });
        command.Parameters.AddWithValue(names[17], row.Version);
        command.Parameters.Add(new NpgsqlParameter(names[18], NpgsqlDbType.Uuid)
        {
            Value = (object?)row.AssignedUserId ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter(names[19], NpgsqlDbType.Text)
        {
            Value = (object?)row.AssignedUserFullName ?? DBNull.Value
        });
    }

    /// <summary>
    /// The bind order for an issue row, in the same order as <see cref="Db.Issue.AllColumns"/>.
    /// Single source for both the parameter binding and the VALUES clause: when these were
    /// written out separately, adding a column updated one and not the other and every insert
    /// failed with "INSERT has more target columns than expressions".
    /// </summary>
    private static string[] IssueParameterNames(string suffix = "") => suffix.Length == 0
        ?
        [
            "id", "group_id", "rule_id", "run_id", "status", "workflow_status", "assigned_to",
            "notes", "external_notes", "priority", "external_received_at_utc",
            "suppression_expires_at_utc", "context", "created_at_utc", "updated_at_utc",
            "activated_at_utc", "deactivated_at_utc", "version", "assigned_user_id", "assigned_user_full_name"
        ]
        :
        [
            $"id{suffix}", $"g{suffix}", $"ru{suffix}", $"rn{suffix}", $"s{suffix}", $"w{suffix}",
            $"a{suffix}", $"n{suffix}", $"xn{suffix}", $"p{suffix}", $"xr{suffix}", $"e{suffix}",
            $"c{suffix}", $"cr{suffix}", $"u{suffix}", $"ac{suffix}", $"de{suffix}", $"v{suffix}",
            $"au{suffix}", $"an{suffix}"
        ];

    internal static string IssueValuesClause(string suffix = "") =>
        $"({string.Join(", ", IssueParameterNames(suffix).Select(name => $"@{name}"))})";

    private static async Task WriteNullableTextAsync(
        NpgsqlBinaryImporter writer,
        string? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }

        await writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
    }

    private static async Task WriteNullableTimestampAsync(
        NpgsqlBinaryImporter writer,
        DateTimeOffset? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }

        await writer.WriteAsync(value.Value, NpgsqlDbType.TimestampTz, cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        return connection;
    }
}
