using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Scenarios;

/// <summary>
/// End-to-end narratives across several nights, driven through the dispatcher by a stand-in
/// rules engine. These are the tests that would catch a regression in the interaction between
/// the machine axis and the human one.
/// </summary>
public class NightlyRunScenarioTests
{
    private static readonly Holding[] Source =
    [
        new("12/345/0001", "J Smith"),
        new("12/345/0002", "A Jones"),
        new("12/345/0003", "P Patel"),
        new("12/345/0004", "R Singh")
    ];

    private static ICleanseRule[] RulesAgainst(params Holding[] target) =>
        [new HoldingMissingInTargetRule(Source, target), new KeeperMismatchRule(Source, target)];

    private static async Task<string[]> ActiveIdsAsync(CleanseHarness harness)
    {
        var page = await harness.QueryAsync(new SearchIssuesQuery(new IssueFilter
        {
            GroupId = TestData.Group,
            Status = IssueStatus.Active,
            Page = new PageRequest(1, 100)
        }));

        return [.. page.Items.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Three_nights_of_detect_fix_and_regress()
    {
        using var harness = new CleanseHarness();
        var engine = new FakeRulesEngine(harness.Dispatcher, TestData.Group);

        // Night 1: only one of the four holdings made it across.
        var night1 = await engine.RunAsync(RulesAgainst(Source[0]));

        Assert.Equal(3, night1.IssuesCreated);
        Assert.Equal(0, night1.IssuesDeactivated);
        Assert.Equal(3, (await ActiveIdsAsync(harness)).Length);

        // Night 2: two more were migrated, so only one anomaly remains.
        harness.Clock.AdvanceDays(1);
        var night2 = await engine.RunAsync(RulesAgainst(Source[0], Source[1], Source[2]));

        Assert.Equal(0, night2.IssuesCreated);
        Assert.Equal(1, night2.IssuesUnchanged);
        Assert.Equal(2, night2.IssuesDeactivated);
        Assert.Equal([TestData.Key("12/345/0004").Value], await ActiveIdsAsync(harness));

        // Night 3: a botched reload drops one of the fixed holdings again.
        harness.Clock.AdvanceDays(1);
        var night3 = await engine.RunAsync(RulesAgainst(Source[0], Source[2]));

        Assert.Equal(0, night3.IssuesCreated);
        Assert.Equal(1, night3.IssuesReactivated);
        Assert.Equal(
            [TestData.Key("12/345/0002").Value, TestData.Key("12/345/0004").Value],
            await ActiveIdsAsync(harness));

        // The reappearance reused the original identity rather than inserting a duplicate.
        Assert.Equal(3, harness.Issues.Count);
    }

    [Fact]
    public async Task A_resolved_issue_that_comes_back_reactivates_but_stays_resolved()
    {
        using var harness = new CleanseHarness();
        var engine = new FakeRulesEngine(harness.Dispatcher, TestData.Group);
        var id = TestData.Key("12/345/0002");

        await engine.RunAsync(RulesAgainst(Source[0]));

        // A caseworker decides the discrepancy is dealt with.
        harness.Clock.Advance(TimeSpan.FromHours(9));
        await harness.SendAsync(new AssignIssueCommand(id, "alice", TestData.Alice));
        await harness.SendAsync(new SetIssueWorkflowStatusCommand(id, WorkflowStatus.Resolved, TestData.Alice));

        // It genuinely disappears for a night, then comes back.
        harness.Clock.AdvanceDays(1);
        await engine.RunAsync(RulesAgainst(Source[0], Source[1], Source[2], Source[3]));
        Assert.Equal("inactive", (await harness.QueryAsync(new GetIssueQuery(id))).Status);

        harness.Clock.AdvanceDays(1);
        await engine.RunAsync(RulesAgainst(Source[0]));

        var issue = await harness.QueryAsync(new GetIssueQuery(id));
        Assert.Equal("active", issue.Status);
        Assert.Equal("resolved", issue.WorkflowStatus);
        Assert.Equal("alice", issue.AssignedTo);
    }

    [Fact]
    public async Task An_ignored_issue_is_never_re_opened_by_the_engine()
    {
        using var harness = new CleanseHarness();
        var engine = new FakeRulesEngine(harness.Dispatcher, TestData.Group);
        var id = TestData.Key("12/345/0002");

        await engine.RunAsync(RulesAgainst(Source[0]));
        await harness.SendAsync(new SetIssueWorkflowStatusCommand(id, WorkflowStatus.Ignored, TestData.Alice));

        for (var night = 0; night < 3; night++)
        {
            harness.Clock.AdvanceDays(1);
            await engine.RunAsync(RulesAgainst(Source[0]));
        }

        Assert.Equal("ignored", (await harness.QueryAsync(new GetIssueQuery(id))).WorkflowStatus);
    }

    [Fact]
    public async Task A_suppression_lapses_on_schedule_while_runs_continue()
    {
        using var harness = new CleanseHarness();
        var engine = new FakeRulesEngine(harness.Dispatcher, TestData.Group);
        var id = TestData.Key("12/345/0002");

        await engine.RunAsync(RulesAgainst(Source[0]));
        await harness.SendAsync(new SuppressIssueCommand(id, TestData.Night1.AddDays(7), TestData.Alice));

        for (var night = 0; night < 5; night++)
        {
            harness.Clock.AdvanceDays(1);
            await engine.RunAsync(RulesAgainst(Source[0]));
            Assert.Equal(0, (await harness.SendAsync(new ExpireSuppressionsCommand(Actor.System))).Expired);
        }

        harness.Clock.AdvanceDays(3);
        await engine.RunAsync(RulesAgainst(Source[0]));
        var expired = await harness.SendAsync(new ExpireSuppressionsCommand(Actor.System));

        Assert.Equal(1, expired.Expired);
        var issue = await harness.QueryAsync(new GetIssueQuery(id));
        Assert.Equal("todo", issue.WorkflowStatus);
        Assert.Equal("active", issue.Status);
    }

    [Fact]
    public async Task A_rule_that_blows_up_abandons_the_run_without_closing_anything()
    {
        using var harness = new CleanseHarness();
        var engine = new FakeRulesEngine(harness.Dispatcher, TestData.Group);
        await engine.RunAsync(RulesAgainst(Source[0]));
        var activeBefore = await ActiveIdsAsync(harness);

        harness.Clock.AdvanceDays(1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunAsync([new ThrowingRule()]));

        Assert.Equal(activeBefore, await ActiveIdsAsync(harness));

        // And the group is not left locked by the failed run.
        var next = await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System));
        Assert.Equal("running", next.State);
    }

    [Fact]
    public async Task A_quiet_night_leaves_no_audit_noise()
    {
        using var harness = new CleanseHarness();
        var engine = new FakeRulesEngine(harness.Dispatcher, TestData.Group);
        await engine.RunAsync(RulesAgainst(Source[0]));

        var id = TestData.Key("12/345/0002");
        var before = (await harness.QueryAsync(new GetIssueAuditQuery(id))).TotalCount;

        for (var night = 0; night < 5; night++)
        {
            harness.Clock.AdvanceDays(1);
            await engine.RunAsync(RulesAgainst(Source[0]));
        }

        var after = await harness.QueryAsync(new GetIssueAuditQuery(id));
        Assert.Equal(before, after.TotalCount);

        // The run id still advanced every night, which is what kept it out of the sweep.
        Assert.Equal("active", (await harness.QueryAsync(new GetIssueQuery(id))).Status);
    }

    [Fact]
    public async Task A_keeper_mismatch_and_a_missing_holding_are_separate_issues()
    {
        using var harness = new CleanseHarness();
        var engine = new FakeRulesEngine(harness.Dispatcher, TestData.Group);

        // 0001 exists but with the wrong keeper; 0002-0004 are missing entirely.
        await engine.RunAsync(RulesAgainst(new Holding("12/345/0001", "WRONG NAME")));

        var mismatch = await harness.QueryAsync(new GetIssueQuery(
            TestData.Key("12/345/0001", TestData.Rule5)));

        Assert.Equal(TestData.Rule5, mismatch.RuleId);
        Assert.Equal("J Smith", mismatch.Context["source.keeper"]);
        Assert.Equal("WRONG NAME", mismatch.Context["target.keeper"]);

        var all = await harness.QueryAsync(new SearchIssuesQuery(new IssueFilter { GroupId = TestData.Group }));
        Assert.Equal(4, all.TotalCount);
    }

    [Fact]
    public async Task A_large_batch_of_detections_upserts_correctly()
    {
        using var harness = new CleanseHarness(configure: o => o.DetectionBatchSize = 250);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        var detections = Enumerable.Range(0, 10_000)
            .Select(i => TestData.Detection($"cph-{i:D5}"))
            .ToArray();

        var first = await harness.SendAsync(new UpsertDetectionsCommand("run-1", detections, Actor.System));
        var replay = await harness.SendAsync(new UpsertDetectionsCommand("run-1", detections, Actor.System));

        Assert.Equal(10_000, first.Created);
        Assert.Equal(10_000, replay.Unchanged);
        Assert.Equal(0, replay.Created);
        Assert.Equal(10_000, harness.Issues.Count);

        // Half of them stop firing; the sweep must close exactly those.
        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await harness.SendAsync(new UpsertDetectionsCommand("run-2", [.. detections.Take(5_000)], Actor.System));
        var completed = await harness.SendAsync(new CompleteRunCommand("run-2", Actor.System));

        Assert.Equal(5_000, completed.IssuesDeactivated);
    }
}
