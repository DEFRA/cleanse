using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.Postgres.Stores;
using Cleanse.Storage.Postgres.Test.Support;

namespace Cleanse.Storage.Postgres.Test;

/// <summary>
/// What the contract suite cannot show from a single connection: several service instances poll
/// the same table, so the guarantees have to hold between transactions, not just within one.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresImportJobConcurrencyTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    private static ImportJob NewJob(string groupId, DateTimeOffset at) =>
        ImportJob.Create(
            Guid.NewGuid(), groupId, "extract.csv", $"imports/{groupId}/{Guid.NewGuid()}.csv", "alice", 10, at);

    [Fact]
    public async Task Concurrent_workers_never_lease_the_same_job()
    {
        var seeding = new PostgresImportJobStore(_harness.NewContext());
        var jobs = Enumerable.Range(0, 40)
            .Select(i => NewJob($"group-{i:D2}", TestData.Night1.AddSeconds(i)))
            .ToArray();
        foreach (var job in jobs)
        {
            Assert.True(await seeding.TryCreateAsync(job));
        }

        // One context per worker, as one per service instance. FOR UPDATE SKIP LOCKED is what
        // keeps them from claiming the same row.
        var workers = Enumerable.Range(0, 8)
            .Select(_ => new PostgresImportJobStore(_harness.NewContext()))
            .ToArray();

        var leasedByWorker = await Task.WhenAll(workers.Select(worker => Task.Run(async () =>
        {
            var leased = new List<Guid>();
            while (await worker.LeaseNextPendingAsync(TestData.Night1.AddHours(1)) is { } job)
            {
                leased.Add(job.Id);
            }

            return leased;
        })));

        var leased = leasedByWorker.SelectMany(ids => ids).ToArray();
        Assert.Equal(jobs.Length, leased.Length);
        Assert.Equal(jobs.Select(j => j.Id).Order(), leased.Order());
    }

    [Fact]
    public async Task Simultaneous_uploads_for_one_group_cannot_both_win()
    {
        // Nothing but the unique partial index stands between two uploads that arrive together.
        var stores = Enumerable.Range(0, 10)
            .Select(_ => new PostgresImportJobStore(_harness.NewContext()))
            .ToArray();

        var created = await Task.WhenAll(stores.Select((store, i) => Task.Run(() =>
            store.TryCreateAsync(NewJob(TestData.Group, TestData.Night1.AddSeconds(i))))));

        Assert.Single(created, won => won);

        var reader = new PostgresImportJobStore(_harness.NewContext());
        Assert.Equal(1, (await reader.SearchAsync(TestData.Group, PageRequest.Default)).TotalCount);
    }

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
