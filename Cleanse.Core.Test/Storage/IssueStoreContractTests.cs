using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Storage;

/// <summary>
/// The behaviour every <see cref="IIssueStore"/> implementation must exhibit. A Postgres, SQLite
/// or Mongo provider inherits this class and implements <see cref="CreateStore"/>; if it passes,
/// Core will behave identically on it. These are the semantics the domain actually relies on,
/// so this suite is the real contract, not the interface signature.
/// </summary>
public abstract class IssueStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;
    private static readonly DateTimeOffset Night2 = Night1.AddDays(1);

    protected abstract IIssueStore CreateStore();

    private async Task<IIssueStore> SeedAsync(params Issue[] issues)
    {
        var store = CreateStore();
        await store.SaveBatchAsync(issues);
        return store;
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_id()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync(TestData.Key("nope")));
    }

    [Fact]
    public async Task Save_then_get_round_trips_every_field()
    {
        var issue = TestData.NewIssue("12/345/6789");
        issue.AssignTo("alice", Night1);
        issue.SetNotes("chased", Night1);
        Assert.True(issue.Suppress(Night1.AddDays(5), Night1).IsSuccess);

        var store = await SeedAsync(issue);
        var loaded = await store.GetAsync(issue.Id);

        Assert.NotNull(loaded);
        Assert.Equal(issue.ToSnapshot(), loaded.ToSnapshot());
    }

    [Fact]
    public async Task Reads_are_isolated_from_each_other()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var id = TestData.Key("12/345/6789");

        var first = await store.GetAsync(id);
        first!.AssignTo("mallory", Night2);

        var second = await store.GetAsync(id);

        Assert.Null(second!.AssignedTo);
    }

    [Fact]
    public async Task Get_many_returns_only_what_exists()
    {
        var store = await SeedAsync(TestData.NewIssue("a"), TestData.NewIssue("b"));

        var found = await store.GetManyAsync([TestData.Key("a"), TestData.Key("missing"), TestData.Key("b")]);

        Assert.Equal(2, found.Count);
        Assert.Equivalent(
            new[] { TestData.Key("a").Value, TestData.Key("b").Value },
            found.Select(i => i.Id.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Get_many_tolerates_an_empty_request()
    {
        var store = CreateStore();

        Assert.Empty(await store.GetManyAsync([]));
    }

    [Fact]
    public async Task Optimistic_save_succeeds_when_the_version_still_matches()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var issue = await store.GetAsync(TestData.Key("12/345/6789"));
        var loadedVersion = issue!.Version;
        issue.AssignTo("alice", Night2);

        Assert.True(await store.SaveAsync(issue, loadedVersion));

        var reloaded = await store.GetAsync(issue.Id);
        Assert.Equal("alice", reloaded!.AssignedTo);
        Assert.Equal(loadedVersion + 1, reloaded.Version);
    }

    [Fact]
    public async Task Optimistic_save_fails_when_somebody_else_got_there_first()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var id = TestData.Key("12/345/6789");

        var alice = await store.GetAsync(id);
        var bob = await store.GetAsync(id);
        var version = alice!.Version;

        alice.AssignTo("alice", Night2);
        Assert.True(await store.SaveAsync(alice, version));

        bob!.AssignTo("bob", Night2);
        Assert.False(await store.SaveAsync(bob, version));

        var winner = await store.GetAsync(id);
        Assert.Equal("alice", winner!.AssignedTo);
    }

    [Fact]
    public async Task A_null_expected_version_writes_unconditionally()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));
        var issue = await store.GetAsync(TestData.Key("12/345/6789"));
        issue!.AssignTo("alice", Night2);
        issue.AssignTo("bob", Night2);

        Assert.True(await store.SaveAsync(issue, expectedVersion: null));

        Assert.Equal("bob", (await store.GetAsync(issue.Id))!.AssignedTo);
    }

    [Fact]
    public async Task Save_batch_inserts_and_updates_in_one_call()
    {
        var existing = TestData.NewIssue("a");
        var store = await SeedAsync(existing);

        existing.AssignTo("alice", Night2);
        await store.SaveBatchAsync([existing, TestData.NewIssue("b")]);

        Assert.Equal("alice", (await store.GetAsync(TestData.Key("a")))!.AssignedTo);
        Assert.NotNull(await store.GetAsync(TestData.Key("b")));
    }

    [Fact]
    public async Task Sweep_deactivates_only_issues_the_current_run_did_not_touch()
    {
        var touched = TestData.NewIssue("touched", runId: "run-2");
        var stale = TestData.NewIssue("stale", runId: "run-1");
        var store = await SeedAsync(touched, stale);

        var deactivated = await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        Assert.Equal(1, deactivated);
        Assert.Equal(IssueStatus.Active, (await store.GetAsync(touched.Id))!.Status);
        Assert.Equal(IssueStatus.Inactive, (await store.GetAsync(stale.Id))!.Status);
    }

    [Fact]
    public async Task Sweep_is_scoped_to_its_group()
    {
        var mine = TestData.NewIssue("mine", runId: "run-1");
        var theirs = TestData.NewIssue("theirs", runId: "run-1", groupId: "other-group");
        var store = await SeedAsync(mine, theirs);

        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        Assert.Equal(IssueStatus.Inactive, (await store.GetAsync(mine.Id))!.Status);
        Assert.Equal(IssueStatus.Active, (await store.GetAsync(theirs.Id))!.Status);
    }

    [Fact]
    public async Task Sweep_leaves_the_human_axis_alone()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        issue.AssignTo("alice", Night1);
        Assert.True(issue.ChangeWorkflowStatus(WorkflowStatus.InProgress, Night1).IsSuccess);
        var store = await SeedAsync(issue);

        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        var swept = await store.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Inactive, swept!.Status);
        Assert.Equal(WorkflowStatus.InProgress, swept.WorkflowStatus);
        Assert.Equal("alice", swept.AssignedTo);
    }

    [Fact]
    public async Task Sweep_does_not_recount_already_inactive_issues()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        var store = await SeedAsync(issue);

        Assert.Equal(1, await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2));
        Assert.Equal(0, await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2));
    }

    /// <summary>
    /// The sweep is set-based SQL rather than a call to Issue.Deactivate, so the two have to be
    /// held to the same outcome explicitly or they will drift.
    /// </summary>
    [Fact]
    public async Task Sweep_records_when_it_deactivated_each_issue()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        var store = await SeedAsync(issue);

        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        var swept = await store.GetAsync(issue.Id);
        Assert.Equal(Night2, swept!.DeactivatedAtUtc);
        Assert.Equal(Night1, swept.ActivatedAtUtc);
    }

    [Fact]
    public async Task A_swept_issue_reactivates_with_a_fresh_activation()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        var store = await SeedAsync(issue);
        await store.DeactivateStaleAsync(TestData.Group, "run-2", Night2);

        var reactivated = await store.GetAsync(issue.Id);
        var third = Night2.AddDays(1);
        reactivated!.RecordDetection("run-3", reactivated.Context, third);
        await store.SaveBatchAsync([reactivated]);

        var stored = await store.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Active, stored!.Status);
        Assert.Equal(third, stored.ActivatedAtUtc);
        Assert.Null(stored.DeactivatedAtUtc);
    }

    [Fact]
    public async Task Expired_suppressions_are_returned_oldest_first_and_capped()
    {
        var store = CreateStore();
        var issues = new List<Issue>();
        for (var i = 0; i < 4; i++)
        {
            var issue = TestData.NewIssue($"cph-{i}");
            Assert.True(issue.Suppress(Night1.AddDays(i + 1), Night1).IsSuccess);
            issues.Add(issue);
        }

        var notYetDue = TestData.NewIssue("cph-later");
        Assert.True(notYetDue.Suppress(Night1.AddDays(30), Night1).IsSuccess);
        issues.Add(notYetDue);
        await store.SaveBatchAsync(issues);

        var due = await store.FindExpiredSuppressionsAsync(Night1.AddDays(4), limit: 2);

        Assert.Equal(2, due.Count);
        Assert.Equal(Night1.AddDays(1), due[0].SuppressionExpiresAtUtc);
        Assert.Equal(Night1.AddDays(2), due[1].SuppressionExpiresAtUtc);
    }

    [Fact]
    public async Task Unsuppressed_issues_are_never_returned_as_expired()
    {
        var store = await SeedAsync(TestData.NewIssue("12/345/6789"));

        Assert.Empty(await store.FindExpiredSuppressionsAsync(Night1.AddYears(1), limit: 10));
    }

    [Fact]
    public async Task Search_filters_on_group_rule_run_and_status()
    {
        var mine = TestData.NewIssue("a", runId: "run-1");
        var otherRule = TestData.NewIssue("b", runId: "run-1", ruleId: TestData.Rule5);
        var otherGroup = TestData.NewIssue("c", runId: "run-1", groupId: "elsewhere");
        var store = await SeedAsync(mine, otherRule, otherGroup);

        var byGroup = await store.SearchAsync(new IssueFilter { GroupId = TestData.Group });
        Assert.Equal(2, byGroup.TotalCount);

        var byRule = await store.SearchAsync(new IssueFilter { RuleId = TestData.Rule5 });
        Assert.Equal(otherRule.Id.Value, Assert.Single(byRule.Items).Id.Value);

        var byRun = await store.SearchAsync(new IssueFilter { RunId = "run-1" });
        Assert.Equal(3, byRun.TotalCount);

        var byStatus = await store.SearchAsync(new IssueFilter { Status = IssueStatus.Inactive });
        Assert.Equal(0, byStatus.TotalCount);
    }

    [Fact]
    public async Task Search_filters_on_the_human_axis()
    {
        var assigned = TestData.NewIssue("a");
        assigned.AssignTo("alice", Night1);
        Assert.True(assigned.ChangeWorkflowStatus(WorkflowStatus.InProgress, Night1).IsSuccess);

        var unassigned = TestData.NewIssue("b");
        var store = await SeedAsync(assigned, unassigned);

        var mine = await store.SearchAsync(new IssueFilter { AssignedTo = "ALICE" });
        Assert.Equal(assigned.Id.Value, Assert.Single(mine.Items).Id.Value);

        var queue = await store.SearchAsync(new IssueFilter { Unassigned = true });
        Assert.Equal(unassigned.Id.Value, Assert.Single(queue.Items).Id.Value);

        var inProgress = await store.SearchAsync(new IssueFilter
        {
            WorkflowStatuses = [WorkflowStatus.InProgress, WorkflowStatus.Resolved]
        });
        Assert.Equal(assigned.Id.Value, Assert.Single(inProgress.Items).Id.Value);
    }

    [Fact]
    public async Task Search_matches_on_context_pairs()
    {
        var match = Issue.FromDetection(
            TestData.Detection("a", TestData.Rule2, ("keeper", "J Smith")), TestData.Group, "run-1", Night1);
        var other = Issue.FromDetection(
            TestData.Detection("b", TestData.Rule2, ("keeper", "A Jones")), TestData.Group, "run-1", Night1);
        var store = await SeedAsync(match, other);

        var found = await store.SearchAsync(new IssueFilter
        {
            ContextMatches = new Dictionary<string, string> { ["keeper"] = "J Smith" }
        });

        Assert.Equal(match.Id.Value, Assert.Single(found.Items).Id.Value);
    }

    [Fact]
    public async Task Search_term_covers_id_notes_assignee_and_context_values()
    {
        var byNotes = TestData.NewIssue("a");
        byNotes.SetNotes("awaiting LETTER from keeper", Night1);
        var byContext = Issue.FromDetection(
            TestData.Detection("b", TestData.Rule2, ("keeper", "Letterman")), TestData.Group, "run-1", Night1);
        var unrelated = TestData.NewIssue("c");
        var store = await SeedAsync(byNotes, byContext, unrelated);

        var found = await store.SearchAsync(new IssueFilter { SearchTerm = "letter" });

        Assert.Equal(2, found.TotalCount);
    }

    [Fact]
    public async Task Search_pages_and_reports_the_unpaged_total()
    {
        var store = CreateStore();
        var issues = Enumerable.Range(0, 5)
            .Select(i => TestData.NewIssue($"cph-{i}", at: Night1.AddMinutes(i)))
            .ToArray();
        await store.SaveBatchAsync(issues);

        var filter = new IssueFilter
        {
            SortBy = IssueSortField.CreatedAtUtc,
            SortDirection = SortDirection.Ascending,
            Page = new PageRequest(2, 2)
        };

        var page = await store.SearchAsync(filter);

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(issues[2].Id.Value, page.Items[0].Id.Value);
        Assert.Equal(issues[3].Id.Value, page.Items[1].Id.Value);
    }

    [Fact]
    public async Task Search_sorts_descending_by_default_on_last_update()
    {
        var older = TestData.NewIssue("older", at: Night1);
        var newer = TestData.NewIssue("newer", at: Night2);
        var store = await SeedAsync(older, newer);

        var page = await store.SearchAsync(new IssueFilter());

        Assert.Equal(newer.Id.Value, page.Items[0].Id.Value);
        Assert.Equal(older.Id.Value, page.Items[1].Id.Value);
    }

    [Fact]
    public async Task Count_by_status_breaks_down_both_axes_for_one_group()
    {
        var active = TestData.NewIssue("a");
        var triaged = TestData.NewIssue("b");
        Assert.True(triaged.ChangeWorkflowStatus(WorkflowStatus.Resolved, Night1).IsSuccess);
        var inactive = TestData.NewIssue("c");
        inactive.Deactivate(Night2);
        var elsewhere = TestData.NewIssue("d", groupId: "elsewhere");
        var store = await SeedAsync(active, triaged, inactive, elsewhere);

        var counts = await store.CountByStatusAsync(TestData.Group);

        Assert.Equal(3, counts.Sum(c => c.Count));
        Assert.Equal(1, counts.Single(c => c is
            { Status: IssueStatus.Active, WorkflowStatus: WorkflowStatus.Resolved }).Count);
        Assert.Equal(1, counts.Single(c => c is
            { Status: IssueStatus.Inactive, WorkflowStatus: WorkflowStatus.Todo }).Count);
    }
}
