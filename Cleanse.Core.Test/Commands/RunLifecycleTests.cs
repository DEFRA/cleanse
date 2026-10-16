using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.Runs;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Commands;

public class RunLifecycleTests
{
    [Fact]
    public async Task Starting_a_run_opens_it_and_audits_it()
    {
        using var harness = new CleanseHarness();

        var run = await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System));

        Assert.Equal("running", run.State);
        Assert.Equal(TestData.Group, run.GroupId);
        Assert.NotEmpty(run.RunId);
        Assert.Equal(TestData.Night1, run.StartedAtUtc);

        var audit = await harness.QueryAsync(new GetRunAuditQuery(run.RunId));
        Assert.Equal(nameof(AuditEventKind.RunStarted), Assert.Single(audit.Items).Kind);
    }

    [Fact]
    public async Task A_group_cannot_have_two_runs_in_flight()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));

        var second = await harness.TrySendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.RunAlreadyInProgress, second.Error.Code);
    }

    [Fact]
    public async Task Other_groups_run_independently()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));

        var other = await harness.SendAsync(new StartRunCommand("other-group", Actor.System, "run-2"));

        Assert.Equal("running", other.State);
    }

    [Fact]
    public async Task A_duplicate_run_id_is_rejected()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        var again = await harness.TrySendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));

        Assert.True(again.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, again.Error.Code);
    }

    [Fact]
    public async Task Completing_sweeps_the_group_and_records_a_single_audit_entry()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("a"), TestData.Detection("b")], Actor.System));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await harness.SendAsync(new UpsertDetectionsCommand("run-2", [TestData.Detection("a")], Actor.System));

        var completed = await harness.SendAsync(new CompleteRunCommand("run-2", Actor.System));

        Assert.Equal("completed", completed.State);
        Assert.Equal(1, completed.IssuesDeactivated);

        var audit = await harness.QueryAsync(new GetRunAuditQuery("run-2"));
        var sweep = Assert.Single(audit.Items, e => e.Kind == nameof(AuditEventKind.RunCompleted));
        Assert.Equal("1", Assert.Single(sweep.Changes).To);
    }

    [Fact]
    public async Task A_completed_run_cannot_be_completed_again()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        var again = await harness.TrySendAsync(new CompleteRunCommand("run-1", Actor.System));

        Assert.True(again.IsFailure);
        Assert.Equal(ErrorCodes.RunNotRunning, again.Error.Code);
    }

    [Fact]
    public async Task An_unknown_run_is_reported_as_not_found()
    {
        using var harness = new CleanseHarness();

        var result = await harness.TrySendAsync(new CompleteRunCommand("ghost", Actor.System));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.RunNotFound, result.Error.Code);
    }

    [Fact]
    public async Task Abandoning_never_sweeps()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("a"), TestData.Detection("b")], Actor.System));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await harness.SendAsync(new UpsertDetectionsCommand("run-2", [TestData.Detection("a")], Actor.System));

        var abandoned = await harness.SendAsync(
            new AbandonRunCommand("run-2", "source system timed out", Actor.System));

        Assert.Equal("abandoned", abandoned.State);
        Assert.Equal(0, abandoned.IssuesDeactivated);

        // 'b' was not re-detected, but a partial run must not be allowed to close it.
        var untouched = await harness.Issues.GetAsync(TestData.Key("b"));
        Assert.Equal(IssueStatus.Active, untouched!.Status);
    }

    [Fact]
    public async Task Abandoning_frees_the_group_for_a_fresh_run()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new AbandonRunCommand("run-1", "boom", Actor.System));

        var next = await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));

        Assert.Equal("running", next.State);
    }

    [Fact]
    public async Task The_latest_run_is_queryable_whatever_its_state()
    {
        using var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new AbandonRunCommand("run-1", "boom", Actor.System));

        var latest = await harness.QueryAsync(new GetLatestRunForGroupQuery(TestData.Group));

        Assert.Equal("run-1", latest.RunId);
        Assert.Equal("abandoned", latest.State);
        Assert.Equal("boom", latest.AbandonReason);
    }

    [Fact]
    public async Task A_group_that_never_ran_reports_not_found()
    {
        using var harness = new CleanseHarness();

        var result = await harness.TryQueryAsync(new GetLatestRunForGroupQuery("never-run"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.RunNotFound, result.Error.Code);
    }
}
