using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteAuditStore(CleanseSqliteDatabase database) : IAuditStore
{
    private const string Columns =
        "id, issue_id, run_id, kind, actor_id, actor_display_name, actor_kind, at_utc, changes";

    private const string Insert = $"""
        INSERT INTO audit_entries ({Columns})
        VALUES ($id, $issue_id, $run_id, $kind, $actor_id, $actor_display_name, $actor_kind, $at_utc, $changes)
        """;

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Insert;
        Bind(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Insert;

        foreach (var entry in entries)
        {
            command.Parameters.Clear();
            Bind(command, entry);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<PagedResult<AuditEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default) =>
        PageAsync("issue_id = $key", issueId.Value, page, cancellationToken);

    public Task<PagedResult<AuditEntry>> GetForRunAsync(
        string runId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return PageAsync("run_id = $key", runId, page, cancellationToken);
    }

    private async Task<PagedResult<AuditEntry>> PageAsync(
        string predicate,
        string key,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = await database.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM audit_entries WHERE {predicate}";
        countCommand.Bind("$key", key);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var pageCommand = connection.CreateCommand();

        // sequence is the stable tie-break: a batch stamps every entry with the same instant.
        pageCommand.CommandText = $"""
            SELECT {Columns} FROM audit_entries
            WHERE {predicate}
            ORDER BY at_utc DESC, sequence DESC
            LIMIT $limit OFFSET $offset
            """;
        pageCommand.Bind("$key", key);
        pageCommand.Bind("$limit", page.PageSize);
        pageCommand.Bind("$offset", page.Skip);

        var items = new List<AuditEntry>(page.PageSize);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(AuditEntry.Rehydrate(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : IssueId.From(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Enum.Parse<AuditEventKind>(reader.GetString(3)),
                SqliteMappings.RebuildActor(reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                SqliteMappings.ToTimestamp(reader.GetString(7)),
                SqliteMappings.DeserialiseChanges(reader.GetString(8))));
        }

        return new PagedResult<AuditEntry>(items, page.PageNumber, page.PageSize, total);
    }

    private static void Bind(SqliteCommand command, AuditEntry entry)
    {
        command.Bind("$id", entry.Id.ToString());
        command.Bind("$issue_id", entry.IssueId?.Value);
        command.Bind("$run_id", entry.RunId);
        command.Bind("$kind", entry.Kind.ToString());
        command.Bind("$actor_id", entry.Actor.Id);
        command.Bind("$actor_display_name", entry.Actor.DisplayName);
        command.Bind("$actor_kind", entry.Actor.Kind.ToString());
        command.Bind("$at_utc", entry.AtUtc.ToText());
        command.Bind("$changes", SqliteMappings.SerialiseChanges(entry.Changes));
    }
}

internal sealed class SqliteIssueHistoryStore(CleanseSqliteDatabase database) : IIssueHistoryStore
{
    private const string Columns =
        "id, issue_id, action_type, comments, created_at_utc, created_by_id, created_by_display_name, created_by_kind";

    public async Task AddAsync(IssueHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var snapshot = entry.ToSnapshot();

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO issue_history ({Columns})
            VALUES ($id, $issue_id, $action_type, $comments, $created_at_utc,
                    $created_by_id, $created_by_display_name, $created_by_kind)
            ON CONFLICT (id) DO UPDATE SET
                action_type = excluded.action_type,
                comments = excluded.comments
            """;

        command.Bind("$id", snapshot.Id.ToString());
        command.Bind("$issue_id", snapshot.IssueId);
        command.Bind("$action_type", snapshot.ActionType.ToString());
        command.Bind("$comments", snapshot.Comments);
        command.Bind("$created_at_utc", snapshot.CreatedAtUtc.ToText());
        command.Bind("$created_by_id", snapshot.CreatedBy.Id);
        command.Bind("$created_by_display_name", snapshot.CreatedBy.DisplayName);
        command.Bind("$created_by_kind", snapshot.CreatedBy.Kind.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IssueHistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM issue_history WHERE id = $id";
        command.Bind("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<PagedResult<IssueHistoryEntry>> GetForIssueAsync(
        IssueId issueId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = await database.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM issue_history WHERE issue_id = $issue_id";
        countCommand.Bind("$issue_id", issueId.Value);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT {Columns} FROM issue_history
            WHERE issue_id = $issue_id
            ORDER BY created_at_utc DESC, sequence DESC
            LIMIT $limit OFFSET $offset
            """;
        pageCommand.Bind("$issue_id", issueId.Value);
        pageCommand.Bind("$limit", page.PageSize);
        pageCommand.Bind("$offset", page.Skip);

        var items = new List<IssueHistoryEntry>(page.PageSize);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return new PagedResult<IssueHistoryEntry>(items, page.PageNumber, page.PageSize, total);
    }

    private static IssueHistoryEntry Read(SqliteDataReader reader) =>
        IssueHistoryEntry.FromSnapshot(new IssueHistorySnapshot(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<HistoryActionType>(reader.GetString(2)),
            reader.GetString(3),
            SqliteMappings.ToTimestamp(reader.GetString(4)),
            SqliteMappings.RebuildActor(reader.GetString(5), reader.GetString(6), reader.GetString(7))));
}

internal sealed class SqliteRuleRunStore(CleanseSqliteDatabase database) : IRuleRunStore
{
    private const string Columns =
        "run_id, group_id, state, started_at_utc, finished_at_utc, abandon_reason, " +
        "detections_processed, issues_created, issues_reactivated, issues_updated, " +
        "issues_unchanged, issues_deactivated, current_stage, records_processed, records_total, " +
        "heartbeat_at_utc, cancellation_requested, failure_message, failure_detail, version";

    public Task<RuleRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return FirstAsync("WHERE run_id = $key", runId, orderBy: null, cancellationToken);
    }

    public Task<RuleRun?> GetActiveForGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        return FirstAsync(
            "WHERE group_id = $key AND state = 'running'", groupId, "started_at_utc DESC", cancellationToken);
    }

    public Task<RuleRun?> GetLatestForGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        return FirstAsync(
            "WHERE group_id = $key", groupId, "started_at_utc DESC, run_id DESC", cancellationToken);
    }

    public async Task<PagedResult<RuleRun>> SearchAsync(
        string? groupId,
        IReadOnlyCollection<RunState>? states,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var clauses = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            clauses.Add("group_id = $group");
        }

        var stateNames = states is { Count: > 0 }
            ? states.Select(s => s.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray()
            : [];

        if (stateNames.Length > 0)
        {
            clauses.Add($"state IN ({string.Join(", ", stateNames.Select((_, i) => $"$state{i}"))})");
        }

        var where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);

        await using var connection = await database.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM rule_runs {where}";
        Bind(countCommand);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM rule_runs {where} " +
            "ORDER BY started_at_utc DESC, run_id DESC LIMIT $take OFFSET $skip";
        Bind(command);
        command.Bind("$take", page.PageSize);
        command.Bind("$skip", page.Skip);

        var items = new List<RuleRun>(page.PageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return new PagedResult<RuleRun>(items, page.PageNumber, page.PageSize, total);

        void Bind(SqliteCommand target)
        {
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                target.Bind("$group", groupId);
            }

            for (var i = 0; i < stateNames.Length; i++)
            {
                target.Bind($"$state{i}", stateNames[i]);
            }
        }
    }

    public async Task<IReadOnlyList<RuleRun>> FindStalledAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM rule_runs " +
            "WHERE state = 'running' AND COALESCE(heartbeat_at_utc, started_at_utc) < $cutoff " +
            "ORDER BY COALESCE(heartbeat_at_utc, started_at_utc) LIMIT $take";
        command.Bind("$cutoff", olderThan.ToText());
        command.Bind("$take", limit);

        var items = new List<RuleRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task SaveAsync(RuleRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var snapshot = run.ToSnapshot();

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO rule_runs ({Columns})
            VALUES ($run_id, $group_id, $state, $started_at_utc, $finished_at_utc, $abandon_reason,
                    $detections_processed, $issues_created, $issues_reactivated, $issues_updated,
                    $issues_unchanged, $issues_deactivated, $current_stage, $records_processed,
                    $records_total, $heartbeat_at_utc, $cancellation_requested, $failure_message,
                    $failure_detail, $version)
            ON CONFLICT (run_id) DO UPDATE SET
                group_id = excluded.group_id,
                state = excluded.state,
                started_at_utc = excluded.started_at_utc,
                finished_at_utc = excluded.finished_at_utc,
                abandon_reason = excluded.abandon_reason,
                detections_processed = excluded.detections_processed,
                issues_created = excluded.issues_created,
                issues_reactivated = excluded.issues_reactivated,
                issues_updated = excluded.issues_updated,
                issues_unchanged = excluded.issues_unchanged,
                issues_deactivated = excluded.issues_deactivated,
                current_stage = excluded.current_stage,
                records_processed = excluded.records_processed,
                records_total = excluded.records_total,
                heartbeat_at_utc = excluded.heartbeat_at_utc,
                cancellation_requested = excluded.cancellation_requested,
                failure_message = excluded.failure_message,
                failure_detail = excluded.failure_detail,
                version = excluded.version
            """;

        command.Bind("$run_id", snapshot.RunId);
        command.Bind("$group_id", snapshot.GroupId);
        command.Bind("$state", snapshot.State.ToString().ToLowerInvariant());
        command.Bind("$started_at_utc", snapshot.StartedAtUtc.ToText());
        command.Bind("$finished_at_utc", snapshot.FinishedAtUtc?.ToText());
        command.Bind("$abandon_reason", snapshot.AbandonReason);
        command.Bind("$detections_processed", snapshot.Counters.DetectionsProcessed);
        command.Bind("$issues_created", snapshot.Counters.IssuesCreated);
        command.Bind("$issues_reactivated", snapshot.Counters.IssuesReactivated);
        command.Bind("$issues_updated", snapshot.Counters.IssuesUpdated);
        command.Bind("$issues_unchanged", snapshot.Counters.IssuesUnchanged);
        command.Bind("$issues_deactivated", snapshot.Counters.IssuesDeactivated);
        command.Bind("$current_stage", snapshot.Progress.CurrentStage);
        command.Bind("$records_processed", snapshot.Progress.RecordsProcessed);
        command.Bind("$records_total", snapshot.Progress.RecordsTotal);
        command.Bind("$heartbeat_at_utc", snapshot.HeartbeatAtUtc?.ToText());
        command.Bind("$cancellation_requested", snapshot.CancellationRequested ? 1 : 0);
        command.Bind("$failure_message", snapshot.Failure?.Message);
        command.Bind("$failure_detail", snapshot.Failure?.Detail);
        command.Bind("$version", snapshot.Version);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<RuleRun?> FirstAsync(
        string where,
        string key,
        string? orderBy,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM rule_runs {where} " +
            (orderBy is null ? string.Empty : $"ORDER BY {orderBy} ") +
            "LIMIT 1";
        command.Bind("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static RuleRun Read(SqliteDataReader reader) =>
        RuleRun.FromSnapshot(new RuleRunSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<RunState>(reader.GetString(2), ignoreCase: true),
            SqliteMappings.ToTimestamp(reader.GetString(3)),
            reader.IsDBNull(4) ? null : SqliteMappings.ToTimestamp(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            new RuleRunCounters(
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11)),
            new RuleRunProgress(
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetInt32(13),
                reader.GetInt32(14)),
            reader.IsDBNull(15) ? null : SqliteMappings.ToTimestamp(reader.GetString(15)),
            reader.GetInt32(16) != 0,
            reader.IsDBNull(17)
                ? null
                : new RuleRunFailure(reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18)),
            reader.GetInt32(19)));
}
