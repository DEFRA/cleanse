using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.Runs;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Commands;

public class UpsertDetectionsTests
{
    private static async Task<CleanseHarness> RunningHarnessAsync(
        string runId = "run-1",
        Action<CleanseCoreOptions>? configure = null)
    {
        var harness = new CleanseHarness(configure: configure);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, runId));
        return harness;
    }

    [Fact]
    public async Task A_first_detection_creates_an_active_issue()
    {
        using var harness = await RunningHarnessAsync();

        var result = await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("12/345/6789")], Actor.System));

        Assert.Equal(new DetectionUpsertResultShape(1, 1, 0, 0, 0), Shape(result));

        var issue = await harness.Issues.GetAsync(TestData.Key("12/345/6789"));
        Assert.Equal(IssueStatus.Active, issue!.Status);
        Assert.Equal(WorkflowStatus.Todo, issue.WorkflowStatus);
        Assert.Equal(TestData.Group, issue.GroupId);
        Assert.Equal("12/345/6789", issue.Context["cph"]);
    }

    [Fact]
    public async Task Re_running_the_same_detection_changes_nothing_and_audits_nothing()
    {
        using var harness = await RunningHarnessAsync();
        var detection = TestData.Detection("12/345/6789");
        await harness.SendAsync(new UpsertDetectionsCommand("run-1", [detection], Actor.System));
        var auditAfterCreate = await harness.QueryAsync(new GetIssueAuditQuery(detection.Id));

        var second = await harness.SendAsync(new UpsertDetectionsCommand("run-1", [detection], Actor.System));

        Assert.Equal(new DetectionUpsertResultShape(1, 0, 0, 0, 1), Shape(second));

        var auditAfterReplay = await harness.QueryAsync(new GetIssueAuditQuery(detection.Id));
        Assert.Equal(auditAfterCreate.TotalCount, auditAfterReplay.TotalCount);
    }

    [Fact]
    public async Task Duplicates_within_one_call_collapse_onto_a_single_issue()
    {
        using var harness = await RunningHarnessAsync();

        var result = await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("dup"), TestData.Detection("dup")], Actor.System));

        Assert.Equal(1, result.DetectionsProcessed);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, harness.Issues.Count);
    }

    [Fact]
    public async Task A_changed_context_is_recorded_as_an_update()
    {
        using var harness = await RunningHarnessAsync();
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("a", TestData.Rule2, ("keeper", "J Smith"))], Actor.System));

        var result = await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("a", TestData.Rule2, ("keeper", "A Jones"))], Actor.System));

        Assert.Equal(1, result.Updated);

        var audit = await harness.QueryAsync(new GetIssueAuditQuery(TestData.Key("a")));
        var change = Assert.Single(audit.Items, e => e.Kind == nameof(AuditEventKind.IssueContextChanged));
        Assert.Equal("context.keeper", Assert.Single(change.Changes).Field);
        Assert.Equal("A Jones", Assert.Single(change.Changes).To);
    }

    [Fact]
    public async Task A_detection_in_a_later_run_reactivates_a_closed_issue()
    {
        using var harness = await RunningHarnessAsync();
        await harness.SendAsync(new UpsertDetectionsCommand("run-1", [TestData.Detection("a")], Actor.System));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await harness.SendAsync(new CompleteRunCommand("run-2", Actor.System));
        Assert.Equal(IssueStatus.Inactive, (await harness.Issues.GetAsync(TestData.Key("a")))!.Status);

        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-3"));
        var result = await harness.SendAsync(new UpsertDetectionsCommand(
            "run-3", [TestData.Detection("a")], Actor.System));

        Assert.Equal(1, result.Reactivated);
        Assert.Equal(IssueStatus.Active, (await harness.Issues.GetAsync(TestData.Key("a")))!.Status);
    }

    [Fact]
    public async Task A_detection_cannot_overwrite_human_triage()
    {
        using var harness = await RunningHarnessAsync();
        var id = TestData.Key("a");
        await harness.SendAsync(new UpsertDetectionsCommand("run-1", [TestData.Detection("a")], Actor.System));
        await harness.SendAsync(new AssignIssueCommand(id, "alice", TestData.Alice));
        await harness.SendAsync(new SetIssueNotesCommand(id, "spoke to the keeper", TestData.Alice));
        await harness.SendAsync(new SetIssueWorkflowStatusCommand(id, WorkflowStatus.Resolved, TestData.Alice));

        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("a", TestData.Rule2, ("keeper", "changed"))], Actor.System));

        var issue = await harness.Issues.GetAsync(id);
        Assert.Equal(WorkflowStatus.Resolved, issue!.WorkflowStatus);
        Assert.Equal("alice", issue.AssignedTo);
        Assert.Equal("spoke to the keeper", issue.Notes);
        Assert.Equal("changed", issue.Context["keeper"]);
    }

    [Fact]
    public async Task A_run_cannot_adopt_another_groups_issue()
    {
        using var harness = await RunningHarnessAsync();
        await harness.SendAsync(new UpsertDetectionsCommand("run-1", [TestData.Detection("a")], Actor.System));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));
        await harness.SendAsync(new StartRunCommand("other-group", Actor.System, "run-2"));

        var result = await harness.TrySendAsync(new UpsertDetectionsCommand(
            "run-2", [TestData.Detection("a")], Actor.System));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueGroupMismatch, result.Error.Code);
    }

    [Fact]
    public async Task Detections_are_refused_once_the_run_is_closed()
    {
        using var harness = await RunningHarnessAsync();
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        var result = await harness.TrySendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("a")], Actor.System));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.RunNotRunning, result.Error.Code);
    }

    [Fact]
    public async Task Detections_against_an_unknown_run_are_refused()
    {
        using var harness = new CleanseHarness();

        var result = await harness.TrySendAsync(new UpsertDetectionsCommand(
            "ghost", [TestData.Detection("a")], Actor.System));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.RunNotFound, result.Error.Code);
    }

    [Fact]
    public async Task An_empty_batch_is_a_no_op()
    {
        using var harness = await RunningHarnessAsync();

        var result = await harness.SendAsync(new UpsertDetectionsCommand("run-1", [], Actor.System));

        Assert.Equal(0, result.DetectionsProcessed);
        Assert.Equal(0, harness.Issues.Count);
    }

    [Fact]
    public async Task Work_spanning_several_chunks_is_counted_once()
    {
        using var harness = await RunningHarnessAsync(configure: o => o.DetectionBatchSize = 2);
        var detections = Enumerable.Range(0, 5).Select(i => TestData.Detection($"cph-{i}")).ToArray();

        var result = await harness.SendAsync(new UpsertDetectionsCommand("run-1", detections, Actor.System));

        Assert.Equal(5, result.DetectionsProcessed);
        Assert.Equal(5, result.Created);
        Assert.Equal(5, harness.Issues.Count);
    }

    [Fact]
    public async Task Counters_accumulate_onto_the_run()
    {
        using var harness = await RunningHarnessAsync();
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("a"), TestData.Detection("b")], Actor.System));
        await harness.SendAsync(new UpsertDetectionsCommand("run-1", [TestData.Detection("a")], Actor.System));

        var run = await harness.QueryAsync(new GetRunQuery("run-1"));

        Assert.Equal(3, run.DetectionsProcessed);
        Assert.Equal(2, run.IssuesCreated);
        Assert.Equal(1, run.IssuesUnchanged);
    }

    [Fact]
    public async Task Detections_from_different_rules_produce_separate_issues()
    {
        using var harness = await RunningHarnessAsync();

        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1",
            [TestData.Detection("12/345/6789", TestData.Rule2), TestData.Detection("12/345/6789", TestData.Rule5)],
            Actor.System));

        Assert.Equal(2, harness.Issues.Count);
        Assert.NotNull(await harness.Issues.GetAsync(TestData.Key("12/345/6789", TestData.Rule2)));
        Assert.NotNull(await harness.Issues.GetAsync(TestData.Key("12/345/6789", TestData.Rule5)));
    }

    private sealed record DetectionUpsertResultShape(
        int Processed, int Created, int Reactivated, int Updated, int Unchanged);

    private static DetectionUpsertResultShape Shape(Core.Contracts.DetectionUpsertResult result) =>
        new(result.DetectionsProcessed, result.Created, result.Reactivated, result.Updated, result.Unchanged);
}
