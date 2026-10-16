using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.DependencyInjection;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.InMemory;
using Cleanse.Storage.Sqlite;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Test;

/// <summary>
/// Core wired to each real storage provider in turn. The per-provider suites prove each store
/// honours the contract in isolation; this proves the use cases built on top behave identically
/// whichever one is plugged in, which is the claim the whole abstraction rests on.
/// </summary>
public sealed class CrossProviderScenarioTests : IAsyncDisposable
{
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<CleanseSqliteDatabase> _databases = [];

    public static TheoryData<string> Providers => new("InMemory", "Sqlite");

    private async Task<(ICleanseDispatcher Dispatcher, TestClock Clock)> BuildAsync(string provider)
    {
        var clock = new TestClock(TestData.Night1);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddCleanseCore();

        if (provider == "Sqlite")
        {
            services.AddCleanseSqliteStorage(options => options.InMemoryName = $"x-{Guid.NewGuid():N}");
        }
        else
        {
            services.AddCleanseInMemoryStorage();
        }

        var built = services.BuildServiceProvider();
        _providers.Add(built);

        if (provider == "Sqlite")
        {
            var database = built.GetRequiredService<CleanseSqliteDatabase>();
            await database.InitialiseAsync();
            _databases.Add(database);
        }

        return (built.GetRequiredService<ICleanseDispatcher>(), clock);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_nightly_cycle_behaves_identically_on_every_provider(string provider)
    {
        var (dispatcher, clock) = await BuildAsync(provider);
        var engine = new FakeRulesEngine(dispatcher, TestData.Group);

        var source = new[]
        {
            new Holding("12/345/0001", "J Smith"),
            new Holding("12/345/0002", "A Jones"),
            new Holding("12/345/0003", "P Patel")
        };

        ICleanseRule[] RulesAgainst(params Holding[] target) =>
            [new HoldingMissingInTargetRule(source, target), new KeeperMismatchRule(source, target)];

        var night1 = await engine.RunAsync(RulesAgainst(source[0]));
        Assert.Equal(2, night1.IssuesCreated);
        Assert.Equal(0, night1.IssuesDeactivated);

        clock.AdvanceDays(1);
        var night2 = await engine.RunAsync(RulesAgainst(source[0], source[1]));
        Assert.Equal(0, night2.IssuesCreated);
        Assert.Equal(1, night2.IssuesDeactivated);

        clock.AdvanceDays(1);
        var night3 = await engine.RunAsync(RulesAgainst(source[0]));
        Assert.Equal(1, night3.IssuesReactivated);

        var active = await dispatcher.QueryAsync(new SearchIssuesQuery(new IssueFilter
        {
            GroupId = TestData.Group,
            Status = IssueStatus.Active
        }));

        Assert.True(active.IsSuccess);
        Assert.Equal(2, active.Value.TotalCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Human_triage_survives_re_detection_on_every_provider(string provider)
    {
        var (dispatcher, clock) = await BuildAsync(provider);
        var id = TestData.Key("12/345/9999");

        var run = await dispatcher.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        Assert.True(run.IsSuccess);

        await dispatcher.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("12/345/9999")], Actor.System));

        await dispatcher.SendAsync(new AssignIssueCommand(id, "alice", TestData.Alice));
        await dispatcher.SendAsync(new SetIssueWorkflowStatusCommand(id, WorkflowStatus.Resolved, TestData.Alice));
        await dispatcher.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        clock.AdvanceDays(1);
        await dispatcher.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await dispatcher.SendAsync(new UpsertDetectionsCommand(
            "run-2", [TestData.Detection("12/345/9999")], Actor.System));
        await dispatcher.SendAsync(new CompleteRunCommand("run-2", Actor.System));

        var issue = await dispatcher.QueryAsync(new GetIssueQuery(id));

        Assert.True(issue.IsSuccess);
        Assert.Equal("active", issue.Value.Status);
        Assert.Equal("resolved", issue.Value.WorkflowStatus);
        Assert.Equal("alice", issue.Value.AssignedTo);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_lapsed_suppression_expires_on_every_provider(string provider)
    {
        var (dispatcher, clock) = await BuildAsync(provider);
        var id = TestData.Key("12/345/8888");

        await dispatcher.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await dispatcher.SendAsync(new UpsertDetectionsCommand(
            "run-1", [TestData.Detection("12/345/8888")], Actor.System));

        await dispatcher.SendAsync(new SuppressIssueCommand(id, TestData.Night1.AddDays(7), TestData.Alice));

        clock.AdvanceDays(3);
        var early = await dispatcher.SendAsync(new ExpireSuppressionsCommand(Actor.System));
        Assert.Equal(0, early.Value.Expired);

        clock.AdvanceDays(5);
        var due = await dispatcher.SendAsync(new ExpireSuppressionsCommand(Actor.System));
        Assert.Equal(1, due.Value.Expired);

        var issue = await dispatcher.QueryAsync(new GetIssueQuery(id));
        Assert.Equal("todo", issue.Value.WorkflowStatus);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }
}
