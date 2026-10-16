using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Commands;

public class SuppressionTests
{
    private static readonly IssueId Id = TestData.Key("12/345/6789");

    private static async Task<CleanseHarness> HarnessWithIssueAsync(Action<CleanseCoreOptions>? configure = null)
    {
        var harness = new CleanseHarness(configure: configure);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("12/345/6789")], Actor.System));
        return harness;
    }

    [Fact]
    public async Task Suppressing_sets_the_status_and_the_expiry()
    {
        using var harness = await HarnessWithIssueAsync();
        var expiry = TestData.Night1.AddDays(7);

        var issue = await harness.SendAsync(new SuppressIssueCommand(Id, expiry, TestData.Alice));

        Assert.Equal("suppressed", issue.WorkflowStatus);
        Assert.Equal(expiry, issue.SuppressionExpiresAtUtc);
    }

    [Fact]
    public async Task An_expiry_in_the_past_is_refused()
    {
        using var harness = await HarnessWithIssueAsync();

        var result = await harness.TrySendAsync(
            new SuppressIssueCommand(Id, TestData.Night1.AddHours(-1), TestData.Alice));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueSuppressionExpiryInPast, result.Error.Code);
    }

    [Fact]
    public async Task Lifting_a_suppression_early_returns_it_to_the_queue()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new SuppressIssueCommand(Id, TestData.Night1.AddDays(7), TestData.Alice));

        var issue = await harness.SendAsync(new UnsuppressIssueCommand(Id, TestData.Alice));

        Assert.Equal("todo", issue.WorkflowStatus);
        Assert.Null(issue.SuppressionExpiresAtUtc);
    }

    [Fact]
    public async Task Lifting_a_suppression_that_was_never_set_is_refused()
    {
        using var harness = await HarnessWithIssueAsync();

        var result = await harness.TrySendAsync(new UnsuppressIssueCommand(Id, TestData.Alice));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueNotSuppressed, result.Error.Code);
    }

    [Fact]
    public async Task The_sweep_leaves_a_suppression_alone_until_it_lapses()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new SuppressIssueCommand(Id, TestData.Night1.AddDays(7), TestData.Alice));

        harness.Clock.AdvanceDays(6);
        var early = await harness.SendAsync(new ExpireSuppressionsCommand(Actor.System));

        Assert.Equal(0, early.Expired);
        Assert.Equal("suppressed", (await harness.QueryAsync(new GetIssueQuery(Id))).WorkflowStatus);
    }

    [Fact]
    public async Task A_lapsed_suppression_returns_to_todo_and_is_audited()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new SuppressIssueCommand(Id, TestData.Night1.AddDays(7), TestData.Alice));

        harness.Clock.AdvanceDays(8);
        var result = await harness.SendAsync(new ExpireSuppressionsCommand(Actor.System));

        Assert.Equal(1, result.Expired);

        var issue = await harness.QueryAsync(new GetIssueQuery(Id));
        Assert.Equal("todo", issue.WorkflowStatus);
        Assert.Null(issue.SuppressionExpiresAtUtc);

        var audit = await harness.QueryAsync(new GetIssueAuditQuery(Id));
        var entry = Assert.Single(audit.Items, e => e.Kind == nameof(AuditEventKind.IssueSuppressionExpired));
        Assert.Equal(Actor.SystemId, entry.ActorId);
    }

    [Fact]
    public async Task The_sweep_is_idempotent()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new SuppressIssueCommand(Id, TestData.Night1.AddDays(7), TestData.Alice));
        harness.Clock.AdvanceDays(8);

        Assert.Equal(1, (await harness.SendAsync(new ExpireSuppressionsCommand(Actor.System))).Expired);
        Assert.Equal(0, (await harness.SendAsync(new ExpireSuppressionsCommand(Actor.System))).Expired);
    }

    [Fact]
    public async Task The_sweep_works_across_several_batches()
    {
        using var harness = await HarnessWithIssueAsync(o => o.SuppressionExpiryBatchSize = 2);
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1",
            [.. Enumerable.Range(0, 5).Select(i => TestData.Detection($"cph-{i}"))],
            Actor.System));

        var expiry = TestData.Night1.AddDays(7);
        for (var i = 0; i < 5; i++)
        {
            await harness.SendAsync(new SuppressIssueCommand(TestData.Key($"cph-{i}"), expiry, TestData.Alice));
        }

        harness.Clock.AdvanceDays(8);
        var result = await harness.SendAsync(new ExpireSuppressionsCommand(Actor.System));

        Assert.Equal(5, result.Expired);
    }

    [Fact]
    public async Task A_suppressed_issue_still_participates_in_the_run_sweep()
    {
        using var harness = await HarnessWithIssueAsync();
        await harness.SendAsync(new SuppressIssueCommand(Id, TestData.Night1.AddDays(7), TestData.Alice));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await harness.SendAsync(new CompleteRunCommand("run-2", Actor.System));

        var issue = await harness.QueryAsync(new GetIssueQuery(Id));
        Assert.Equal("inactive", issue.Status);
        Assert.Equal("suppressed", issue.WorkflowStatus);
    }
}
