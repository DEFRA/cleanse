using Cleanse.Core.Commands.History;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.History;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Commands;

public class IssueCommandTests
{
    private static readonly IssueId Id = TestData.Key("12/345/6789");

    private static async Task<CleanseHarness> HarnessWithIssueAsync()
    {
        var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("12/345/6789")], Actor.System));
        return harness;
    }

    [Fact]
    public async Task Assigning_records_who_did_it()
    {
        using var harness = await HarnessWithIssueAsync();

        var issue = await harness.SendAsync(new AssignIssueCommand(Id, "bob", TestData.Alice));

        Assert.Equal("bob", issue.AssignedTo);

        var audit = await harness.QueryAsync(new GetIssueAuditQuery(Id));
        var entry = Assert.Single(audit.Items, e => e.Kind == nameof(AuditEventKind.IssueAssigned));
        Assert.Equal("alice", entry.ActorId);
        Assert.Equal("human", entry.ActorKind);
        Assert.Equal("bob", Assert.Single(entry.Changes).To);
    }

    [Fact]
    public async Task Re_assigning_to_the_same_person_writes_no_audit_entry()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new AssignIssueCommand(Id, "bob", TestData.Alice));
        var before = (await harness.QueryAsync(new GetIssueAuditQuery(Id))).TotalCount;

        var issue = await harness.SendAsync(new AssignIssueCommand(Id, "bob", TestData.Alice));

        Assert.Equal("bob", issue.AssignedTo);
        Assert.Equal(before, (await harness.QueryAsync(new GetIssueAuditQuery(Id))).TotalCount);
    }

    [Fact]
    public async Task The_workflow_status_moves_along_the_allowed_path()
    {
        using var harness = await HarnessWithIssueAsync();

        var inProgress = await harness.SendAsync(
            new SetIssueWorkflowStatusCommand(Id, WorkflowStatus.InProgress, TestData.Alice));
        Assert.Equal("in-progress", inProgress.WorkflowStatus);

        var resolved = await harness.SendAsync(
            new SetIssueWorkflowStatusCommand(Id, WorkflowStatus.Resolved, TestData.Alice));
        Assert.Equal("resolved", resolved.WorkflowStatus);
    }

    [Fact]
    public async Task An_illegal_transition_is_refused()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new SetIssueWorkflowStatusCommand(Id, WorkflowStatus.Ignored, TestData.Alice));

        var result = await harness.TrySendAsync(
            new SetIssueWorkflowStatusCommand(Id, WorkflowStatus.Resolved, TestData.Alice));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueInvalidTransition, result.Error.Code);
    }

    [Fact]
    public async Task Suppression_cannot_be_reached_without_an_expiry()
    {
        using var harness = await HarnessWithIssueAsync();

        var result = await harness.TrySendAsync(
            new SetIssueWorkflowStatusCommand(Id, WorkflowStatus.Suppressed, TestData.Alice));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error.Code);
    }

    [Fact]
    public async Task Commands_against_a_missing_issue_report_not_found()
    {
        using var harness = new CleanseHarness();

        var result = await harness.TrySendAsync(new AssignIssueCommand(TestData.Key("ghost"), "bob", TestData.Alice));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueNotFound, result.Error.Code);
    }

    [Fact]
    public async Task A_stale_expected_version_is_rejected()
    {
        using var harness = await HarnessWithIssueAsync();
        var original = await harness.QueryAsync(new GetIssueQuery(Id));

        await harness.SendAsync(new AssignIssueCommand(Id, "bob", TestData.Alice, original.Version));

        var stale = await harness.TrySendAsync(
            new SetIssueNotesCommand(Id, "written against an old copy", TestData.Alice, original.Version));

        Assert.True(stale.IsFailure);
        Assert.Equal(ErrorCodes.IssueConcurrencyConflict, stale.Error.Code);
        Assert.Null((await harness.QueryAsync(new GetIssueQuery(Id))).Notes);
    }

    [Fact]
    public async Task Omitting_the_expected_version_lets_the_last_writer_win()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new AssignIssueCommand(Id, "bob", TestData.Alice));

        var issue = await harness.SendAsync(new SetIssueNotesCommand(Id, "fine", TestData.Alice));

        Assert.Equal("fine", issue.Notes);
    }

    [Fact]
    public async Task History_entries_are_recorded_against_the_issue()
    {
        using var harness = await HarnessWithIssueAsync();

        var entry = await harness.SendAsync(new AddIssueHistoryCommand(
            Id, HistoryActionType.LetterSent, "  posted the first reminder  ", TestData.Alice));

        Assert.Equal(nameof(HistoryActionType.LetterSent), entry.ActionType);
        Assert.Equal("posted the first reminder", entry.Comments);
        Assert.Equal("alice", entry.CreatedById);
        Assert.Equal(TestData.Night1, entry.CreatedAtUtc);

        var history = await harness.QueryAsync(new GetIssueHistoryQuery(Id));
        Assert.Equal(entry.Id, Assert.Single(history.Items).Id);

        var audit = await harness.QueryAsync(new GetIssueAuditQuery(Id));
        Assert.Contains(audit.Items, e => e.Kind == nameof(AuditEventKind.IssueHistoryAdded));
    }

    [Fact]
    public async Task History_needs_an_issue_and_a_comment()
    {
        using var harness = await HarnessWithIssueAsync();

        var missing = await harness.TrySendAsync(new AddIssueHistoryCommand(
            TestData.Key("ghost"), HistoryActionType.Comment, "hello", TestData.Alice));
        Assert.Equal(ErrorCodes.IssueNotFound, missing.Error!.Code);

        var blank = await harness.TrySendAsync(new AddIssueHistoryCommand(
            Id, HistoryActionType.Comment, "   ", TestData.Alice));
        Assert.Equal(ErrorCodes.ValidationFailed, blank.Error!.Code);
    }

    [Fact]
    public async Task The_audit_trail_reads_back_newest_first()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new AssignIssueCommand(Id, "bob", TestData.Alice));
        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new SetIssueNotesCommand(Id, "chased", TestData.Alice));

        var audit = await harness.QueryAsync(new GetIssueAuditQuery(Id));

        Assert.Equal(
            [
                nameof(AuditEventKind.IssueNotesChanged),
                nameof(AuditEventKind.IssueAssigned),
                nameof(AuditEventKind.IssueCreated)
            ],
            audit.Items.Select(e => e.Kind).ToArray());
    }
}
