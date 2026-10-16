using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.Sqlite.Stores;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Test;

/// <summary>
/// Behaviours that depend on a real SQL engine rather than the contract alone. The Postgres
/// provider asserts the same guarantees, but those tests need a container; these run anywhere.
/// </summary>
public sealed class SqliteSpecificTests : IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();

    [Fact]
    public async Task A_large_batch_upserts_rather_than_duplicating()
    {
        var store = _harness.Issues;
        var issues = Enumerable.Range(0, 10_000)
            .Select(i => TestData.NewIssue($"cph-{i:D5}", runId: "run-1"))
            .ToArray();

        await store.SaveBatchAsync(issues);
        await store.SaveBatchAsync(issues);

        var counts = await store.CountByStatusAsync(TestData.Group);
        Assert.Equal(10_000, counts.Sum(c => c.Count));

        var deactivated = await store.DeactivateStaleAsync(
            TestData.Group, "run-2", TestData.Night1.AddDays(1));
        Assert.Equal(10_000, deactivated);
    }

    [Fact]
    public async Task A_batch_spanning_several_transactions_is_applied_in_full()
    {
        var database = _harness.Database;
        var store = new SqliteIssueStore(database, new CleanseSqliteOptions { BatchSize = 7 });
        var issues = Enumerable.Range(0, 50).Select(i => TestData.NewIssue($"cph-{i:D3}")).ToArray();

        await store.SaveBatchAsync(issues);

        var all = await store.SearchAsync(new IssueFilter { Page = new PageRequest(1, 100) });
        Assert.Equal(50, all.TotalCount);
    }

    [Fact]
    public async Task Context_matching_uses_json_values()
    {
        var store = _harness.Issues;
        await store.SaveBatchAsync(
        [
            Issue.FromDetection(
                TestData.Detection("a", TestData.Rule2, ("keeper", "J Smith")),
                TestData.Group, "run-1", TestData.Night1),
            Issue.FromDetection(
                TestData.Detection("b", TestData.Rule2, ("keeper", "A Jones")),
                TestData.Group, "run-1", TestData.Night1)
        ]);

        var found = await store.SearchAsync(new IssueFilter
        {
            ContextMatches = new Dictionary<string, string> { ["keeper"] = "J Smith" }
        });

        Assert.Equal(TestData.Key("a").Value, Assert.Single(found.Items).Id.Value);
    }

    [Fact]
    public async Task A_search_term_cannot_inject_sql_or_wildcards()
    {
        var store = _harness.Issues;
        var issue = TestData.NewIssue("a");
        issue.SetNotes("100% complete", TestData.Night1);
        await store.SaveBatchAsync([issue, TestData.NewIssue("b")]);

        // A literal % must match only the row that actually contains one. If the term were not
        // escaped it would behave as a wildcard and match every row.
        var wildcard = await store.SearchAsync(new IssueFilter { SearchTerm = "%" });
        Assert.Equal(issue.Id.Value, Assert.Single(wildcard.Items).Id.Value);

        Assert.Equal(1, (await store.SearchAsync(new IssueFilter { SearchTerm = "100%" })).TotalCount);
        Assert.Equal(
            0,
            (await store.SearchAsync(new IssueFilter { SearchTerm = "'; DROP TABLE issues; --" })).TotalCount);

        Assert.Equal(2, (await store.SearchAsync(new IssueFilter())).TotalCount);
    }

    [Fact]
    public async Task The_search_term_matches_context_values_but_not_context_keys()
    {
        var store = _harness.Issues;
        await store.SaveBatchAsync(
        [
            Issue.FromDetection(
                TestData.Detection("a", TestData.Rule2, ("keeper", "value-here")),
                TestData.Group, "run-1", TestData.Night1)
        ]);

        Assert.Equal(1, (await store.SearchAsync(new IssueFilter { SearchTerm = "value-here" })).TotalCount);
        Assert.Equal(0, (await store.SearchAsync(new IssueFilter { SearchTerm = "keeper" })).TotalCount);
    }

    [Fact]
    public async Task The_database_refuses_a_second_running_run_for_one_group()
    {
        var store = _harness.Runs;
        await store.SaveAsync(RuleRun.Start("run-1", TestData.Group, TestData.Night1));

        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            store.SaveAsync(RuleRun.Start("run-2", TestData.Group, TestData.Night1)));

        // SQLite names the offending columns rather than the index. 19 is SQLITE_CONSTRAINT.
        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Contains("rule_runs.group_id", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_completed_run_frees_the_group_for_the_next_one()
    {
        var store = _harness.Runs;
        var first = RuleRun.Start("run-1", TestData.Group, TestData.Night1);
        await store.SaveAsync(first);

        Assert.True(first.Complete(0, TestData.Night1.AddHours(1)).IsSuccess);
        await store.SaveAsync(first);
        await store.SaveAsync(RuleRun.Start("run-2", TestData.Group, TestData.Night1.AddDays(1)));

        Assert.Equal("run-2", (await store.GetActiveForGroupAsync(TestData.Group))!.RunId);
    }

    [Fact]
    public async Task A_status_outside_the_allowed_set_is_rejected_by_the_check_constraint()
    {
        await using var connection = await _harness.Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO issues (id, group_id, rule_id, run_id, status, workflow_status,
                                context, created_at_utc, updated_at_utc, activated_at_utc, version)
            VALUES ('x', 'g', 'r', 'run', 'nonsense', 'todo', '{}', '2026-01-01', '2026-01-01',
                    '2026-01-01', 1)
            """;

        var failure = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("CHECK constraint failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_for_an_issue_that_does_not_exist_is_rejected()
    {
        // Proves PRAGMA foreign_keys is actually applied; SQLite ignores the constraint without it.
        var failure = await Assert.ThrowsAsync<SqliteException>(() => _harness.History.AddAsync(
            IssueHistoryEntry.Create(
                TestData.Key("orphan"), HistoryActionType.Comment, "no parent", TestData.Alice, TestData.Night1)));

        Assert.Contains("FOREIGN KEY constraint failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_an_issue_cascades_to_its_history()
    {
        var issue = TestData.NewIssue("a");
        await _harness.Issues.SaveBatchAsync([issue]);
        await _harness.History.AddAsync(IssueHistoryEntry.Create(
            issue.Id, HistoryActionType.LetterSent, "posted", TestData.Alice, TestData.Night1));

        await using (var connection = await _harness.Database.OpenAsync())
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM issues WHERE id = $id";
            delete.Parameters.AddWithValue("$id", issue.Id.Value);
            await delete.ExecuteNonQueryAsync();
        }

        var remaining = await _harness.History.GetForIssueAsync(issue.Id, PageRequest.Default);
        Assert.Equal(0, remaining.TotalCount);
    }

    [Fact]
    public async Task Timestamps_survive_the_round_trip_as_utc()
    {
        var store = _harness.Issues;
        var issue = TestData.NewIssue("a");
        Assert.True(issue.Suppress(TestData.Night1.AddDays(7), TestData.Night1).IsSuccess);
        await store.SaveBatchAsync([issue]);

        var loaded = await store.GetAsync(issue.Id);

        Assert.Equal(issue.CreatedAtUtc, loaded!.CreatedAtUtc);
        Assert.Equal(issue.SuppressionExpiresAtUtc, loaded.SuppressionExpiresAtUtc);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAtUtc.Offset);
    }

    [Fact]
    public async Task Timestamps_stored_as_text_still_range_filter_correctly()
    {
        // Text storage only sorts correctly because every value is written in a fixed-width
        // round-trip UTC format. A regression there would show up here first.
        var store = _harness.Issues;
        var issues = Enumerable.Range(0, 12)
            .Select(i => TestData.NewIssue($"cph-{i:D2}", at: TestData.Night1.AddMonths(i)))
            .ToArray();
        await store.SaveBatchAsync(issues);

        var window = await store.SearchAsync(new IssueFilter
        {
            CreatedAfterUtc = TestData.Night1.AddMonths(3),
            CreatedBeforeUtc = TestData.Night1.AddMonths(6),
            SortBy = IssueSortField.CreatedAtUtc,
            SortDirection = SortDirection.Ascending
        });

        Assert.Equal(4, window.TotalCount);
        Assert.Equal(TestData.Key("cph-03").Value, window.Items[0].Id.Value);
        Assert.Equal(TestData.Key("cph-06").Value, window.Items[^1].Id.Value);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

/// <summary>
/// The reset is the one place that issues set-based deletes across every table, and the scoped
/// variant resolves the group through tables it is about to empty. Order is the whole game.
/// </summary>
public sealed class SqliteDataResetTests : IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();

    private const string Other = "other-group";

    private async Task SeedAsync(string groupId, string runId, string cph)
    {
        await _harness.Runs.SaveAsync(RuleRun.Start(runId, groupId, TestData.Night1));

        var issue = TestData.NewIssue(cph, runId: runId, groupId: groupId);
        await _harness.Issues.SaveBatchAsync([issue]);

        await _harness.History.AddAsync(IssueHistoryEntry.Create(
            issue.Id, HistoryActionType.PhoneCall, "called the keeper", TestData.Alice, TestData.Night1));

        await _harness.Audit.AppendBatchAsync(
        [
            AuditEntry.ForRun(runId, AuditEventKind.RunStarted, TestData.Alice, TestData.Night1),
            AuditEntry.ForIssue(
                issue.Id,
                new IssueChange(AuditEventKind.IssueCreated, [new AuditChange("status", null, "active")]),
                TestData.Alice,
                TestData.Night1)
        ]);
    }

    [Fact]
    public async Task A_full_reset_empties_every_table()
    {
        await SeedAsync(TestData.Group, "run-1", "a");
        await SeedAsync(Other, "run-2", "b");

        var counts = await _harness.Reset.ResetAsync(groupId: null, "tester", TestData.Night1);

        Assert.Equal(2, counts.Issues);
        Assert.Equal(2, counts.IssueHistoryEntries);
        Assert.Equal(4, counts.AuditEntries);
        Assert.Equal(2, counts.RuleRuns);

        Assert.Equal(0, (await _harness.Issues.SearchAsync(new IssueFilter())).TotalCount);
        Assert.Null(await _harness.Runs.GetAsync("run-1"));
        Assert.Equal(
            0,
            (await _harness.Audit.GetForRunAsync("run-1", PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task A_scoped_reset_takes_the_group_and_nothing_else()
    {
        await SeedAsync(TestData.Group, "run-1", "a");
        await SeedAsync(Other, "run-2", "b");

        var counts = await _harness.Reset.ResetAsync(TestData.Group, "tester", TestData.Night1);

        Assert.Equal(1, counts.Issues);
        Assert.Equal(1, counts.IssueHistoryEntries);
        Assert.Equal(2, counts.AuditEntries);
        Assert.Equal(1, counts.RuleRuns);

        Assert.Null(await _harness.Issues.GetAsync(TestData.Key("a")));
        Assert.Null(await _harness.Runs.GetAsync("run-1"));

        var survivor = TestData.Key("b");
        Assert.NotNull(await _harness.Issues.GetAsync(survivor));
        Assert.NotNull(await _harness.Runs.GetAsync("run-2"));
        Assert.Equal(
            1,
            (await _harness.History.GetForIssueAsync(survivor, PageRequest.Default)).TotalCount);
        Assert.Equal(
            1,
            (await _harness.Audit.GetForIssueAsync(survivor, PageRequest.Default)).TotalCount);
        Assert.Equal(
            1,
            (await _harness.Audit.GetForRunAsync("run-2", PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task A_reset_that_matches_nothing_deletes_nothing()
    {
        await SeedAsync(TestData.Group, "run-1", "a");

        var counts = await _harness.Reset.ResetAsync("no-such-group", "tester", TestData.Night1);

        Assert.Equal(0, counts.Issues);
        Assert.Equal(0, counts.AuditEntries);
        Assert.NotNull(await _harness.Issues.GetAsync(TestData.Key("a")));
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
