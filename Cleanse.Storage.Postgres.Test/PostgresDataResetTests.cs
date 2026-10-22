using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.Postgres.Stores;
using Cleanse.Storage.Postgres.Test.Support;
using Npgsql;

namespace Cleanse.Storage.Postgres.Test;

/// <summary>
/// The reset is the one place that issues set-based deletes across every table, the scoped
/// variant resolves the group through tables it is about to empty, and on CDP it runs as a role
/// that may not TRUNCATE. The fixture connects as that role, so all three are tested as deployed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresDataResetTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Other = "other-group";

    private readonly PostgresStoreHarness _harness = new(fixture);

    private PostgresDataResetStore Reset => new(_harness.NewContext());

    private PostgresIssueStore Issues => new(_harness.NewContext(), CleansePostgresOptions.ForExistingDataSource());

    private PostgresRuleRunStore Runs => new(_harness.NewContext());

    private PostgresIssueHistoryStore History => new(_harness.NewContext());

    private PostgresAuditStore Audit => new(_harness.NewContext());

    private PostgresImportJobStore Imports => new(_harness.NewContext());

    /// <summary>One of everything a reset deletes, in one group.</summary>
    private async Task<ImportJob> SeedAsync(string groupId, string runId, string cph)
    {
        await Runs.SaveAsync(RuleRun.Start(runId, groupId, TestData.Night1));

        var issue = TestData.NewIssue(cph, runId: runId, groupId: groupId);
        await Issues.SaveBatchAsync([issue]);

        await History.AddAsync(IssueHistoryEntry.Create(
            issue.Id, HistoryActionType.PhoneCall, "called the keeper", TestData.Alice, TestData.Night1));

        await Audit.AppendBatchAsync(
        [
            AuditEntry.ForRun(runId, AuditEventKind.RunStarted, TestData.Alice, TestData.Night1),
            AuditEntry.ForIssue(
                issue.Id,
                new IssueChange(AuditEventKind.IssueCreated, [new AuditChange("status", null, "active")]),
                TestData.Alice,
                TestData.Night1)
        ]);

        var job = ImportJob.Create(
            Guid.NewGuid(), groupId, "extract.csv", $"imports/{groupId}/{runId}.csv", "alice", 1, TestData.Night1);
        var imports = Imports;
        Assert.True(await imports.TryCreateAsync(job));
        await imports.AddRejectionsAsync(
            [new ImportJobRejection(job.Id, 1, null, ImportRejectionReasons.InvalidRow, "CPH is missing.")]);

        return job;
    }

    [Fact]
    public async Task A_full_reset_empties_every_table_and_records_itself()
    {
        await SeedAsync(TestData.Group, "run-1", "a");
        await SeedAsync(Other, "run-2", "b");

        var counts = await Reset.ResetAsync(groupId: null, "tester", TestData.Night1);

        Assert.Equal(new ResetCounts(2, 2, 4, 2, 2, 2), counts);

        Assert.Equal(0, (await Issues.SearchAsync(new IssueFilter())).TotalCount);
        Assert.Null(await Runs.GetAsync("run-1"));
        Assert.Equal(0, (await Audit.GetForRunAsync("run-1", PageRequest.Default)).TotalCount);
        Assert.Equal(0, (await Imports.SearchAsync(groupId: null, PageRequest.Default)).TotalCount);

        Assert.Equal(
            new DataResetRecord(TestData.Night1, "all", "tester", 2),
            await Reset.GetLastResetAsync());
    }

    [Fact]
    public async Task A_scoped_reset_takes_the_group_and_nothing_else()
    {
        await SeedAsync(TestData.Group, "run-1", "a");
        var survivingJob = await SeedAsync(Other, "run-2", "b");

        var counts = await Reset.ResetAsync(TestData.Group, "tester", TestData.Night1);

        Assert.Equal(new ResetCounts(1, 1, 2, 1, 1, 1), counts);

        Assert.Null(await Issues.GetAsync(TestData.Key("a")));
        Assert.Null(await Runs.GetAsync("run-1"));
        Assert.Null(await Imports.GetActiveForGroupAsync(TestData.Group));

        var survivor = TestData.Key("b");
        Assert.NotNull(await Issues.GetAsync(survivor));
        Assert.NotNull(await Runs.GetAsync("run-2"));
        Assert.Equal(1, (await History.GetForIssueAsync(survivor, PageRequest.Default)).TotalCount);
        Assert.Equal(1, (await Audit.GetForIssueAsync(survivor, PageRequest.Default)).TotalCount);
        Assert.Equal(1, (await Audit.GetForRunAsync("run-2", PageRequest.Default)).TotalCount);
        Assert.Equal(survivingJob.Id, (await Imports.GetActiveForGroupAsync(Other))!.Id);
        Assert.Equal(1, (await Imports.GetRejectionsAsync(survivingJob.Id, PageRequest.Default)).TotalCount);

        Assert.Equal(
            new DataResetRecord(TestData.Night1, TestData.Group, "tester", 1),
            await Reset.GetLastResetAsync());
    }

    [Fact]
    public async Task A_reset_that_matches_nothing_deletes_nothing_but_is_still_recorded()
    {
        await SeedAsync(TestData.Group, "run-1", "a");

        var counts = await Reset.ResetAsync("no-such-group", "tester", TestData.Night1);

        Assert.Equal(new ResetCounts(0, 0, 0, 0, 0, 0), counts);
        Assert.NotNull(await Issues.GetAsync(TestData.Key("a")));
        Assert.Equal("no-such-group", (await Reset.GetLastResetAsync())!.Scope);
    }

    [Fact]
    public async Task The_reset_log_survives_the_resets_it_records()
    {
        Assert.Null(await Reset.GetLastResetAsync());

        await Reset.ResetAsync(groupId: null, "first", TestData.Night1);
        await Reset.ResetAsync(groupId: null, "second", TestData.Night1.AddDays(1));

        Assert.Equal(
            new DataResetRecord(TestData.Night1.AddDays(1), "all", "second", 0),
            await Reset.GetLastResetAsync());
    }

    [Fact]
    public async Task The_service_role_may_not_truncate_which_is_why_the_reset_deletes()
    {
        _harness.NewContext();
        await using var command = fixture.DataSource.CreateCommand("TRUNCATE TABLE issues");

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
    }

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
