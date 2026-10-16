using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.DependencyInjection;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Queries.Runs;
using Cleanse.Rules.Abstractions;
using Cleanse.Rules.DependencyInjection;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Engine;
using Cleanse.Rules.Test.Support;
using Cleanse.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Test;

/// <summary>
/// Hosts the engine over real Core and the in-memory provider, with only the data source faked.
/// Nothing else is substituted, so these exercise the same path a deployed run takes.
/// </summary>
internal sealed class EngineHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    public EngineHarness(ICtsSamDataSourceFactory dataSource, int batchSize = 500)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddCleanseCore();
        services.AddCleanseInMemoryStorage();
        services.TryAddSingleton(dataSource);

        services.AddCleanseRules(new CleanseRulesOptions
        {
            Enabled = false,
            GroupId = CleanseRuleRegistry.CtsSamGroupId,
            BatchSize = batchSize
        });

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = false
        });
    }

    public ICleanseDispatcher Dispatcher => _provider.GetRequiredService<ICleanseDispatcher>();

    public CtsSamRuleEngine Engine => _provider.GetRequiredService<CtsSamRuleEngine>();

    public async Task<string> StartRunAsync()
    {
        var started = await Dispatcher.SendAsync(
            new StartRunCommand(CleanseRuleRegistry.CtsSamGroupId, Actor.System));
        Assert.True(started.IsSuccess, $"{started.Error}");
        return started.Value.RunId;
    }

    public async Task<RuleEngineResult> RunAsync()
    {
        var runId = await StartRunAsync();
        return await Engine.ExecuteAsync(runId, Actor.System);
    }

    public async Task<Cleanse.Core.Contracts.RuleRunDto> GetRunAsync(string runId)
    {
        var run = await Dispatcher.QueryAsync(new GetRunQuery(runId));
        Assert.True(run.IsSuccess, $"{run.Error}");
        return run.Value;
    }

    public async Task<IReadOnlyList<Cleanse.Core.Contracts.IssueDto>> GetIssuesAsync(string? ruleId = null)
    {
        var filter = new IssueFilter
        {
            GroupId = CleanseRuleRegistry.CtsSamGroupId,
            RuleId = ruleId,
            Page = new PageRequest(1, 500)
        };

        var page = await Dispatcher.QueryAsync(new SearchIssuesQuery(filter));
        Assert.True(page.IsSuccess, $"{page.Error}");
        return page.Value.Items;
    }

    public async Task AssignAsync(string issueId, string assignee)
    {
        var result = await Dispatcher.SendAsync(new Core.Commands.Issues.AssignIssueCommand(
            IssueId.From(issueId), assignee, Actor.Human(assignee)));
        Assert.True(result.IsSuccess, $"{result.Error}");
    }

    public void Dispose() => _provider.Dispose();
}

public class CtsSamRuleEngineTests
{
    [Fact]
    public async Task Raises_one_issue_per_rule_that_fires()
    {
        var source = new FakeDataSource(
            Records.InBoth("01/001/0001"),              // clean
            Records.CtsOnly("02/002/0002"),             // rule 2
            Records.InBoth("03/003/0003", "SHP"));      // rule 1

        using var harness = new EngineHarness(source);
        var result = await harness.RunAsync();

        Assert.Equal(RuleEngineOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.RecordsScanned);
        Assert.Equal(2, result.FindingsRaised);
        Assert.Equal(2, result.IssuesCreated);

        Assert.Single(await harness.GetIssuesAsync("2"));
        Assert.Single(await harness.GetIssuesAsync("1"));
    }

    [Fact]
    public async Task Writes_the_rule_identity_and_the_cph_onto_every_issue()
    {
        var source = new FakeDataSource(Records.CtsOnly("02/002/0002"));

        using var harness = new EngineHarness(source);
        await harness.RunAsync();

        var issue = Assert.Single(await harness.GetIssuesAsync());

        Assert.Equal(IssueId.Derive("2", "02/002/0002").Value, issue.Id);
        Assert.Equal("2", issue.RuleId);
        Assert.Equal("02/002/0002", issue.Context["cph"]);
        Assert.Equal("2", issue.Context["ruleNumber"]);
        Assert.Equal("02", issue.Context["errorCode"]);
        Assert.Equal("CTS-CPH-missing-in-Sam", issue.Context["errorDescription"]);
        Assert.Equal("AH-02/002/0002", issue.Context["ctsLocationNumber"]);
    }

    [Fact]
    public async Task Re_detecting_the_same_anomaly_creates_nothing_new()
    {
        var source = new FakeDataSource(Records.CtsOnly("02/002/0002"));
        using var harness = new EngineHarness(source);

        await harness.RunAsync();
        var second = await harness.RunAsync();

        Assert.Equal(0, second.IssuesCreated);
        Assert.Equal(0, second.IssuesDeactivated);
        Assert.Single(await harness.GetIssuesAsync());
    }

    [Fact]
    public async Task Sweeps_issues_the_run_no_longer_detects()
    {
        var source = new FakeDataSource(Records.CtsOnly("02/002/0002"));
        using var harness = new EngineHarness(source);

        await harness.RunAsync();
        Assert.Equal("active", Assert.Single(await harness.GetIssuesAsync()).Status);

        // The anomaly is fixed overnight: the CPH now exists in SAM, with a cattle unit.
        source.Records = [Records.InBoth("02/002/0002")];
        var second = await harness.RunAsync();

        Assert.Equal(0, second.FindingsRaised);
        Assert.Equal(1, second.IssuesDeactivated);
        Assert.Equal("inactive", Assert.Single(await harness.GetIssuesAsync()).Status);
    }

    [Fact]
    public async Task A_swept_issue_comes_back_with_its_triage_state_intact()
    {
        var source = new FakeDataSource(Records.CtsOnly("02/002/0002"));
        using var harness = new EngineHarness(source);

        await harness.RunAsync();
        await harness.AssignAsync(IssueId.Derive("2", "02/002/0002").Value, "alice");

        source.Records = [Records.InBoth("02/002/0002")];
        await harness.RunAsync();

        source.Records = [Records.CtsOnly("02/002/0002")];
        var third = await harness.RunAsync();

        Assert.Equal(1, third.IssuesReactivated);

        var issue = Assert.Single(await harness.GetIssuesAsync());
        Assert.Equal("active", issue.Status);
        Assert.Equal("alice", issue.AssignedTo);
    }

    [Fact]
    public async Task Records_progress_and_a_hundred_percent_on_completion()
    {
        var source = new FakeDataSource([.. Enumerable.Range(1, 10)
            .Select(i => Records.CtsOnly($"02/002/{i:0000}"))]);

        using var harness = new EngineHarness(source, batchSize: 2);
        var runId = await harness.StartRunAsync();
        await harness.Engine.ExecuteAsync(runId, Actor.System);
        var run = await harness.GetRunAsync(runId);

        Assert.Equal("completed", run.State);
        Assert.Equal(10, run.RecordsTotal);
        Assert.Equal(10, run.RecordsProcessed);
        Assert.Equal(100d, run.ProgressPercent);

        // A finished run is in no stage. Reporting the last one read as though the sweep were
        // still going, which is what it looked like on the admin page.
        Assert.Null(run.CurrentStage);
    }

    [Fact]
    public async Task Disposes_the_data_source_so_a_downloaded_snapshot_is_released()
    {
        var source = new FakeDataSource(Records.InBoth());

        using var harness = new EngineHarness(source);
        await harness.RunAsync();

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task Fails_the_run_with_the_full_technical_detail()
    {
        var source = new FakeDataSource(Records.InBoth())
        {
            FailOnOpen = new InvalidOperationException("The snapshot is unreadable.")
        };

        using var harness = new EngineHarness(source);
        var runId = await harness.StartRunAsync();

        var result = await harness.Engine.ExecuteAsync(runId, Actor.System);

        Assert.Equal(RuleEngineOutcome.Failed, result.Outcome);

        var run = await harness.GetRunAsync(runId);
        Assert.Equal("failed", run.State);
        Assert.Equal("The snapshot is unreadable.", run.FailureMessage);
        Assert.Contains(nameof(InvalidOperationException), run.FailureDetail);
    }

    [Fact]
    public async Task A_failed_run_leaves_existing_issues_active()
    {
        var source = new FakeDataSource(Records.CtsOnly("02/002/0002"));
        using var harness = new EngineHarness(source);

        await harness.RunAsync();

        // A failed run must not sweep: the snapshot never arrived, so nothing was re-detected,
        // and a group-wide deactivation would wrongly discard every issue.
        source.FailOnOpen = new IOException("The download failed.");
        var runId = await harness.StartRunAsync();
        await harness.Engine.ExecuteAsync(runId, Actor.System);

        Assert.Equal("active", Assert.Single(await harness.GetIssuesAsync()).Status);
    }

    [Fact]
    public async Task Stops_at_a_batch_boundary_when_cancellation_is_requested()
    {
        var source = new FakeDataSource([.. Enumerable.Range(1, 10)
            .Select(i => Records.CtsOnly($"02/002/{i:0000}"))]);

        using var harness = new EngineHarness(source, batchSize: 2);
        var runId = await harness.StartRunAsync();

        var requested = await harness.Dispatcher.SendAsync(
            new RequestRunCancellationCommand(runId, Actor.System));
        Assert.True(requested.IsSuccess);

        var result = await harness.Engine.ExecuteAsync(runId, Actor.System);

        Assert.Equal(RuleEngineOutcome.Cancelled, result.Outcome);
        Assert.Equal(2, result.RecordsScanned);

        var run = await harness.GetRunAsync(runId);
        Assert.Equal("cancelled", run.State);

        // No sweep: the two issues it did find stay active rather than being deactivated.
        Assert.Equal(2, (await harness.GetIssuesAsync()).Count);
    }

    [Fact]
    public async Task Refuses_a_second_concurrent_run_for_the_group()
    {
        using var harness = new EngineHarness(new FakeDataSource(Records.InBoth()));
        await harness.StartRunAsync();

        var second = await harness.Dispatcher.SendAsync(
            new StartRunCommand(CleanseRuleRegistry.CtsSamGroupId, Actor.System));

        Assert.True(second.IsFailure);
        Assert.Equal(Core.ErrorCodes.RunAlreadyInProgress, second.Error.Code);
    }
}
