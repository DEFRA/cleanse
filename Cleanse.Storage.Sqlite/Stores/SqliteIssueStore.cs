using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteIssueStore(CleanseSqliteDatabase database, CleanseSqliteOptions options) : IIssueStore
{
    private const string Columns =
        "id, group_id, rule_id, run_id, status, workflow_status, assigned_to, notes, " +
        "external_notes, priority, external_received_at_utc, suppression_expires_at_utc, " +
        "context, created_at_utc, updated_at_utc, activated_at_utc, deactivated_at_utc, version";

    private const string UpsertAssignments = """
            group_id = excluded.group_id,
            rule_id = excluded.rule_id,
            run_id = excluded.run_id,
            status = excluded.status,
            workflow_status = excluded.workflow_status,
            assigned_to = excluded.assigned_to,
            notes = excluded.notes,
            external_notes = excluded.external_notes,
            priority = excluded.priority,
            external_received_at_utc = excluded.external_received_at_utc,
            suppression_expires_at_utc = excluded.suppression_expires_at_utc,
            context = excluded.context,
            updated_at_utc = excluded.updated_at_utc,
            activated_at_utc = excluded.activated_at_utc,
            deactivated_at_utc = excluded.deactivated_at_utc,
            version = excluded.version
        """;

    public async Task<Issue?> GetAsync(IssueId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM issues WHERE id = $id";
        command.Bind("$id", id.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Issue.FromSnapshot(SqliteMappings.ReadIssue(reader))
            : null;
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

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var names = new List<string>(ids.Count);
        var index = 0;
        foreach (var id in ids)
        {
            var name = $"$id{index++}";
            names.Add(name);
            command.Bind(name, id.Value);
        }

        command.CommandText = $"SELECT {Columns} FROM issues WHERE id IN ({string.Join(", ", names)})";

        var found = new List<Issue>(ids.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(Issue.FromSnapshot(SqliteMappings.ReadIssue(reader)));
        }

        return found;
    }

    public async Task<bool> SaveAsync(
        Issue issue,
        int? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // SQLite's UPSERT accepts a WHERE on the DO UPDATE, so a stale caller changes no rows
        // and learns it lost the race, matching the Postgres provider exactly.
        var guard = expectedVersion is null ? string.Empty : "WHERE issues.version = $expected_version";
        command.CommandText = $"""
            INSERT INTO issues ({Columns})
            VALUES ($id, $group_id, $rule_id, $run_id, $status, $workflow_status, $assigned_to, $notes,
                    $external_notes, $priority, $external_received_at_utc, $suppression_expires_at_utc,
                        $context, $created_at_utc, $updated_at_utc, $activated_at_utc,
                        $deactivated_at_utc, $version)
            ON CONFLICT (id) DO UPDATE SET
            {UpsertAssignments}
            {guard}
            """;

        Bind(command, issue.ToSnapshot());
        if (expectedVersion is { } expected)
        {
            command.Bind("$expected_version", expected);
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

        await using var connection = await database.OpenAsync(cancellationToken);

        foreach (var chunk in issues.Chunk(options.BatchSize))
        {
            // One transaction per chunk. SQLite commits per statement otherwise, which turns a
            // batch into one fsync per row.
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO issues ({Columns})
                VALUES ($id, $group_id, $rule_id, $run_id, $status, $workflow_status, $assigned_to, $notes,
                        $external_notes, $priority, $external_received_at_utc, $suppression_expires_at_utc,
                        $context, $created_at_utc, $updated_at_utc, $activated_at_utc,
                        $deactivated_at_utc, $version)
                ON CONFLICT (id) DO UPDATE SET
                {UpsertAssignments}
                """;

            foreach (var issue in chunk)
            {
                command.Parameters.Clear();
                Bind(command, issue.ToSnapshot());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<int> DeactivateStaleAsync(
        string groupId,
        string currentRunId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunId);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // Mirrors Issue.Deactivate: status, both timestamps and version move together.
        command.CommandText = """
            UPDATE issues
            SET status = $inactive,
                updated_at_utc = $at,
                deactivated_at_utc = $at,
                version = version + 1
            WHERE group_id = $group_id
              AND status = $active
              AND run_id <> $run_id
            """;

        command.Bind("$inactive", StatusNames.Inactive);
        command.Bind("$active", StatusNames.Active);
        command.Bind("$at", at.ToText());
        command.Bind("$group_id", groupId);
        command.Bind("$run_id", currentRunId);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Issue>> FindExpiredSuppressionsAsync(
        DateTimeOffset at,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM issues
            WHERE workflow_status = $suppressed
              AND suppression_expires_at_utc IS NOT NULL
              AND suppression_expires_at_utc <= $at
            ORDER BY suppression_expires_at_utc ASC, id ASC
            LIMIT $limit
            """;

        command.Bind("$suppressed", StatusNames.Suppressed);
        command.Bind("$at", at.ToText());
        command.Bind("$limit", limit);

        var matches = new List<Issue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add(Issue.FromSnapshot(SqliteMappings.ReadIssue(reader)));
        }

        return matches;
    }

    public async Task<PagedResult<Issue>> SearchAsync(
        IssueFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var clause = SqliteIssueFilter.Build(filter);
        await using var connection = await database.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM issues {clause.Sql}";
        clause.Bind(countCommand);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        if (total == 0)
        {
            return PagedResult<Issue>.Empty(filter.Page);
        }

        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT {Columns} FROM issues
            {clause.Sql}
            ORDER BY {SqliteIssueFilter.OrderBy(filter)}
            LIMIT $limit OFFSET $offset
            """;
        clause.Bind(pageCommand);
        pageCommand.Bind("$limit", filter.Page.PageSize);
        pageCommand.Bind("$offset", filter.Page.Skip);

        var items = new List<Issue>(filter.Page.PageSize);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Issue.FromSnapshot(SqliteMappings.ReadIssue(reader)));
        }

        return new PagedResult<Issue>(items, filter.Page.PageNumber, filter.Page.PageSize, total);
    }

    public async Task<IReadOnlyList<IssueStatusCount>> CountByStatusAsync(
        string? groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT status, workflow_status, COUNT(*)
            FROM issues
            {(groupId is null ? string.Empty : "WHERE group_id = $group_id")}
            GROUP BY status, workflow_status
            """;

        if (groupId is not null)
        {
            command.Bind("$group_id", groupId);
        }

        var counts = new List<IssueStatusCount>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                counts.Add(new IssueStatusCount(
                    SqliteMappings.ParseIssueStatus(reader.GetString(0)),
                    SqliteMappings.ParseWorkflowStatus(reader.GetString(1)),
                    reader.GetInt32(2)));
            }
        }

        return [.. counts.OrderBy(c => c.Status).ThenBy(c => c.WorkflowStatus)];
    }

    private static void Bind(SqliteCommand command, IssueSnapshot snapshot)
    {
        command.Bind("$id", snapshot.Id);
        command.Bind("$group_id", snapshot.GroupId);
        command.Bind("$rule_id", snapshot.RuleId);
        command.Bind("$run_id", snapshot.RunId);
        command.Bind("$status", snapshot.Status.ToWire());
        command.Bind("$workflow_status", snapshot.WorkflowStatus.ToWire());
        command.Bind("$assigned_to", snapshot.AssignedTo);
        command.Bind("$notes", snapshot.Notes);
        command.Bind("$external_notes", snapshot.ExternalNotes);
        command.Bind("$priority", snapshot.Priority?.ToWire());
        command.Bind("$external_received_at_utc", snapshot.ExternalReceivedAtUtc?.ToText());
        command.Bind("$suppression_expires_at_utc", snapshot.SuppressionExpiresAtUtc?.ToText());
        command.Bind("$context", SqliteMappings.SerialiseContext(snapshot.Context));
        command.Bind("$created_at_utc", snapshot.CreatedAtUtc.ToText());
        command.Bind("$updated_at_utc", snapshot.UpdatedAtUtc.ToText());
        command.Bind("$activated_at_utc", snapshot.ActivatedAtUtc.ToText());
        command.Bind("$deactivated_at_utc", snapshot.DeactivatedAtUtc?.ToText());
        command.Bind("$version", snapshot.Version);
    }
}
