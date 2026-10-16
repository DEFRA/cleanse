using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Domain;

public class IssueTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;
    private static readonly DateTimeOffset Night2 = Night1.AddDays(1);

    [Fact]
    public void New_issue_starts_active_and_todo()
    {
        var issue = TestData.NewIssue("12/345/6789");

        Assert.Equal(IssueStatus.Active, issue.Status);
        Assert.Equal(WorkflowStatus.Todo, issue.WorkflowStatus);
        Assert.Null(issue.AssignedTo);
        Assert.Null(issue.Notes);
        Assert.Equal(1, issue.Version);
        Assert.Equal(Night1, issue.CreatedAtUtc);

        // First detection: there is no distinction between raised and activated.
        Assert.Equal(Night1, issue.ActivatedAtUtc);
        Assert.Null(issue.DeactivatedAtUtc);
    }

    [Fact]
    public void Deactivation_records_when_it_happened()
    {
        var issue = TestData.NewIssue("12/345/6789");

        issue.Deactivate(Night2);

        Assert.Equal(Night2, issue.DeactivatedAtUtc);

        // The activation it is closing off stays put, so the open period is still readable.
        Assert.Equal(Night1, issue.ActivatedAtUtc);
    }

    [Fact]
    public void Reactivation_moves_the_activation_forward_and_clears_the_deactivation()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        issue.Deactivate(Night2);

        var third = Night2.AddDays(1);
        issue.RecordDetection("run-3", issue.Context, third);

        Assert.Equal(third, issue.ActivatedAtUtc);
        Assert.Null(issue.DeactivatedAtUtc);
    }

    [Fact]
    public void An_issue_can_cycle_in_and_out_of_activity()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");

        issue.Deactivate(Night2);
        issue.RecordDetection("run-3", issue.Context, Night2.AddDays(1));
        issue.Deactivate(Night2.AddDays(2));

        // Only the most recent pair is kept; the audit trail holds the full sequence.
        Assert.Equal(Night2.AddDays(1), issue.ActivatedAtUtc);
        Assert.Equal(Night2.AddDays(2), issue.DeactivatedAtUtc);
    }

    [Fact]
    public void Re_detection_of_an_active_issue_leaves_the_activation_alone()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");

        issue.RecordDetection("run-2", IssueContext.From(("cph", "changed")), Night2);

        Assert.Equal(Night1, issue.ActivatedAtUtc);
        Assert.Null(issue.DeactivatedAtUtc);
    }

    [Fact]
    public void Re_detection_with_identical_context_is_immaterial()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        var before = issue.Version;

        var change = issue.RecordDetection("run-2", issue.Context, Night2);

        Assert.False(change.IsMaterial);
        Assert.Equal(before, issue.Version);
        Assert.Equal(Night1, issue.UpdatedAtUtc);

        // The run id still advances, otherwise the end-of-run sweep would close this issue.
        Assert.Equal("run-2", issue.RunId);
    }

    [Fact]
    public void Re_detection_after_deactivation_reactivates()
    {
        var issue = TestData.NewIssue("12/345/6789", runId: "run-1");
        issue.Deactivate(Night2);

        var change = issue.RecordDetection("run-3", issue.Context, Night2.AddDays(1));

        Assert.Equal(AuditEventKind.IssueReactivated, change.Kind);
        Assert.Equal(IssueStatus.Active, issue.Status);
    }

    [Fact]
    public void Re_detection_reports_context_differences()
    {
        var issue = TestData.NewIssue("12/345/6789");
        var context = IssueContext.From(("cph", "12/345/6789"), ("keeper", "J Smith"));

        var change = issue.RecordDetection("run-2", context, Night2);

        Assert.True(change.IsMaterial);
        var added = Assert.Single(change.Changes);
        Assert.Equal("context.keeper", added.Field);
        Assert.Null(added.From);
        Assert.Equal("J Smith", added.To);
    }

    [Theory]
    [InlineData(WorkflowStatus.Resolved)]
    [InlineData(WorkflowStatus.Ignored)]
    [InlineData(WorkflowStatus.InProgress)]
    public void Re_detection_never_touches_the_human_axis(WorkflowStatus triaged)
    {
        var issue = TestData.NewIssue("12/345/6789");
        issue.AssignTo("alice", Night1);
        issue.SetNotes("chased the keeper", Night1);
        Assert.True(issue.ChangeWorkflowStatus(triaged, Night1).IsSuccess);

        issue.Deactivate(Night2);
        issue.RecordDetection("run-9", IssueContext.From(("cph", "x")), Night2.AddDays(1));

        Assert.Equal(IssueStatus.Active, issue.Status);
        Assert.Equal(triaged, issue.WorkflowStatus);
        Assert.Equal("alice", issue.AssignedTo);
        Assert.Equal("chased the keeper", issue.Notes);
    }

    [Fact]
    public void Suppression_survives_re_detection_including_its_expiry()
    {
        var issue = TestData.NewIssue("12/345/6789");
        var expiry = Night1.AddDays(7);
        Assert.True(issue.Suppress(expiry, Night1).IsSuccess);

        issue.RecordDetection("run-2", issue.Context, Night2);

        Assert.Equal(WorkflowStatus.Suppressed, issue.WorkflowStatus);
        Assert.Equal(expiry, issue.SuppressionExpiresAtUtc);
    }

    [Fact]
    public void Deactivating_an_inactive_issue_is_immaterial()
    {
        var issue = TestData.NewIssue("12/345/6789");
        Assert.True(issue.Deactivate(Night2).IsMaterial);

        var second = issue.Deactivate(Night2.AddDays(1));

        Assert.False(second.IsMaterial);
        Assert.Equal(Night2, issue.UpdatedAtUtc);
    }

    [Fact]
    public void Assigning_the_same_person_twice_is_immaterial()
    {
        var issue = TestData.NewIssue("12/345/6789");
        issue.AssignTo("alice", Night1);
        var version = issue.Version;

        Assert.False(issue.AssignTo("  alice  ", Night2).IsMaterial);
        Assert.Equal(version, issue.Version);
    }

    [Fact]
    public void Assigning_blank_unassigns()
    {
        var issue = TestData.NewIssue("12/345/6789");
        issue.AssignTo("alice", Night1);

        var change = issue.AssignTo("   ", Night2);

        Assert.True(change.IsMaterial);
        Assert.Null(issue.AssignedTo);
    }

    [Fact]
    public void Suppress_rejects_an_expiry_in_the_past()
    {
        var issue = TestData.NewIssue("12/345/6789");

        var result = issue.Suppress(Night1.AddHours(-1), Night1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueSuppressionExpiryInPast, result.Error.Code);
        Assert.Equal(WorkflowStatus.Todo, issue.WorkflowStatus);
    }

    [Fact]
    public void Workflow_status_cannot_be_set_to_suppressed_without_an_expiry()
    {
        var issue = TestData.NewIssue("12/345/6789");

        var result = issue.ChangeWorkflowStatus(WorkflowStatus.Suppressed, Night1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error.Code);
    }

    [Fact]
    public void Leaving_suppression_clears_the_expiry()
    {
        var issue = TestData.NewIssue("12/345/6789");
        Assert.True(issue.Suppress(Night1.AddDays(7), Night1).IsSuccess);

        var result = issue.ChangeWorkflowStatus(WorkflowStatus.Resolved, Night2);

        Assert.True(result.IsSuccess);
        Assert.Null(issue.SuppressionExpiresAtUtc);
        Assert.Contains(result.Value.Changes, c => c.Field == nameof(Issue.SuppressionExpiresAtUtc));
    }

    [Fact]
    public void Expire_suppression_only_fires_once_the_expiry_has_passed()
    {
        var issue = TestData.NewIssue("12/345/6789");
        var expiry = Night1.AddDays(7);
        Assert.True(issue.Suppress(expiry, Night1).IsSuccess);

        Assert.False(issue.ExpireSuppression(expiry.AddSeconds(-1)).IsMaterial);
        Assert.Equal(WorkflowStatus.Suppressed, issue.WorkflowStatus);

        var change = issue.ExpireSuppression(expiry);

        Assert.Equal(AuditEventKind.IssueSuppressionExpired, change.Kind);
        Assert.Equal(WorkflowStatus.Todo, issue.WorkflowStatus);
        Assert.Null(issue.SuppressionExpiresAtUtc);
    }

    [Fact]
    public void Unsuppress_fails_when_the_issue_is_not_suppressed()
    {
        var issue = TestData.NewIssue("12/345/6789");

        var result = issue.Unsuppress(Night1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueNotSuppressed, result.Error.Code);
    }

    [Fact]
    public void Snapshot_round_trip_preserves_every_field()
    {
        var issue = TestData.NewIssue("12/345/6789");
        issue.AssignTo("alice", Night1);
        issue.SetNotes("note", Night1);
        Assert.True(issue.Suppress(Night1.AddDays(3), Night1).IsSuccess);

        var restored = Issue.FromSnapshot(issue.ToSnapshot());

        Assert.Equal(issue.ToSnapshot(), restored.ToSnapshot());
    }
}
