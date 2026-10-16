using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.Postgres.Stores;
using Cleanse.Storage.Postgres.Test.Support;
using Npgsql;

namespace Cleanse.Storage.Postgres.Test;

/// <summary>
/// Behaviours that only exist on a real database: the COPY path, index-backed containment, and
/// the constraints the in-memory provider cannot enforce.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresSpecificTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    private PostgresIssueStore IssueStore(int copyThreshold = 100) =>
        new(_harness.NewContext(), CleansePostgresOptions.ForExistingDataSource(copyThreshold));

    [Fact]
    public async Task The_copy_path_and_the_insert_path_produce_identical_rows()
    {
        var issues = Enumerable.Range(0, 20)
            .Select(i => TestData.NewIssue($"cph-{i:D3}"))
            .ToArray();

        var viaInsert = IssueStore(copyThreshold: 1000);
        await viaInsert.SaveBatchAsync(issues);
        var inserted = await viaInsert.GetManyAsync([.. issues.Select(i => i.Id)]);

        await fixture.ResetAsync();

        var viaCopy = IssueStore(copyThreshold: 1);
        await viaCopy.SaveBatchAsync(issues);
        var copied = await viaCopy.GetManyAsync([.. issues.Select(i => i.Id)]);

        Assert.Equal(
            inserted.OrderBy(i => i.Id.Value, StringComparer.Ordinal).Select(i => i.ToSnapshot()),
            copied.OrderBy(i => i.Id.Value, StringComparer.Ordinal).Select(i => i.ToSnapshot()));
    }

    [Fact]
    public async Task The_copy_path_upserts_rather_than_duplicating()
    {
        var store = IssueStore(copyThreshold: 1);
        var issues = Enumerable.Range(0, 10).Select(i => TestData.NewIssue($"cph-{i}")).ToArray();

        await store.SaveBatchAsync(issues);
        foreach (var issue in issues)
        {
            issue.AssignTo("alice", TestData.Night1.AddDays(1));
        }

        await store.SaveBatchAsync(issues);

        var all = await store.SearchAsync(new IssueFilter { Page = new PageRequest(1, 100) });
        Assert.Equal(10, all.TotalCount);
        Assert.All(all.Items, i => Assert.Equal("alice", i.AssignedTo));
    }

    [Fact]
    public async Task A_ten_thousand_row_batch_round_trips()
    {
        var store = IssueStore(copyThreshold: 100);
        var issues = Enumerable.Range(0, 10_000)
            .Select(i => TestData.NewIssue($"cph-{i:D5}", runId: "run-1"))
            .ToArray();

        await store.SaveBatchAsync(issues);

        var counts = await store.CountByStatusAsync(TestData.Group);
        Assert.Equal(10_000, counts.Sum(c => c.Count));

        var deactivated = await store.DeactivateStaleAsync(
            TestData.Group, "run-2", TestData.Night1.AddDays(1));
        Assert.Equal(10_000, deactivated);
    }

    [Fact]
    public async Task Context_containment_is_served_by_the_gin_index()
    {
        var store = IssueStore();
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
        var store = IssueStore();
        var issue = TestData.NewIssue("a");
        issue.SetNotes("100% complete", TestData.Night1);
        await store.SaveBatchAsync([issue, TestData.NewIssue("b")]);

        // A literal % must match only the row that actually contains one. If the term were not
        // escaped it would behave as a wildcard and match every row.
        var wildcard = await store.SearchAsync(new IssueFilter { SearchTerm = "%" });
        Assert.Equal(issue.Id.Value, Assert.Single(wildcard.Items).Id.Value);

        var literal = await store.SearchAsync(new IssueFilter { SearchTerm = "100%" });
        Assert.Equal(1, literal.TotalCount);

        var injection = await store.SearchAsync(new IssueFilter { SearchTerm = "'; DROP TABLE issues; --" });
        Assert.Equal(0, injection.TotalCount);

        // The table is still there.
        Assert.Equal(2, (await store.SearchAsync(new IssueFilter())).TotalCount);
    }

    [Fact]
    public async Task The_search_term_matches_context_values_but_not_context_keys()
    {
        var store = IssueStore();
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
        var store = new PostgresRuleRunStore(_harness.NewContext());
        await store.SaveAsync(RuleRun.Start("run-1", TestData.Group, TestData.Night1));

        // The domain guards this too, but only the unique partial index stops two schedulers
        // that raced past that check.
        var second = new PostgresRuleRunStore(_harness.NewContext());
        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            second.SaveAsync(RuleRun.Start("run-2", TestData.Group, TestData.Night1)));

        Assert.Contains(
            "ux_rule_runs_one_running_per_group",
            failure.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_completed_run_frees_the_group_for_the_next_one()
    {
        var store = new PostgresRuleRunStore(_harness.NewContext());
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
        _harness.NewContext();
        await using var command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO issues (id, group_id, rule_id, run_id, status, workflow_status,
                                context, created_at_utc, updated_at_utc, activated_at_utc, version)
            VALUES ('x', 'g', 'r', 'run', 'nonsense', 'todo', '{}', now(), now(), now(), 1)
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", failure.SqlState);
        Assert.Contains("ck_issues_status", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_for_an_issue_that_does_not_exist_is_rejected()
    {
        var history = new PostgresIssueHistoryStore(_harness.NewContext());

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => history.AddAsync(
            IssueHistoryEntry.Create(
                TestData.Key("orphan"), HistoryActionType.Comment, "no parent", TestData.Alice, TestData.Night1)));

        Assert.Contains("fk_issue_history_issue", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_an_issue_cascades_to_its_history()
    {
        var issues = IssueStore();
        var issue = TestData.NewIssue("a");
        await issues.SaveBatchAsync([issue]);

        var history = new PostgresIssueHistoryStore(_harness.NewContext());
        await history.AddAsync(IssueHistoryEntry.Create(
            issue.Id, HistoryActionType.LetterSent, "posted", TestData.Alice, TestData.Night1));

        await using (var delete = fixture.DataSource.CreateCommand("DELETE FROM issues WHERE id = $1"))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = issue.Id.Value });
            await delete.ExecuteNonQueryAsync();
        }

        var remaining = await history.GetForIssueAsync(issue.Id, PageRequest.Default);
        Assert.Equal(0, remaining.TotalCount);
    }

    [Fact]
    public async Task Timestamps_survive_the_round_trip_as_utc()
    {
        var store = IssueStore();
        var issue = TestData.NewIssue("a");
        Assert.True(issue.Suppress(TestData.Night1.AddDays(7), TestData.Night1).IsSuccess);
        await store.SaveBatchAsync([issue]);

        var loaded = await store.GetAsync(issue.Id);

        Assert.Equal(issue.CreatedAtUtc, loaded!.CreatedAtUtc);
        Assert.Equal(issue.SuppressionExpiresAtUtc, loaded.SuppressionExpiresAtUtc);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAtUtc.Offset);
    }

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
