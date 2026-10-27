using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Storage;

/// <summary>
/// The behaviour every <see cref="IImportJobStore"/> must exhibit. The import worker relies on a
/// lease going to one worker only and on a group having one active import at a time, so those are
/// held here against every provider rather than against whichever one a test host happens to use.
/// </summary>
public abstract class ImportJobStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;

    protected abstract IImportJobStore CreateStore();

    private static ImportJob NewJob(string groupId = TestData.Group, DateTimeOffset? at = null, int totalRows = 10) =>
        ImportJob.Create(
            Guid.NewGuid(),
            groupId,
            "extract.csv",
            $"imports/{groupId}/{Guid.NewGuid()}.csv",
            "alice",
            totalRows,
            at ?? Night1);

    /// <summary>Takes a job through to completion, which frees its group for the next one.</summary>
    private static async Task<ImportJob> SeedFinishedAsync(IImportJobStore store, string groupId, DateTimeOffset at)
    {
        var job = NewJob(groupId, at);
        Assert.True(await store.TryCreateAsync(job));
        Assert.True(job.Start(at).IsSuccess);
        Assert.True(job.Complete(at.AddMinutes(1)).IsSuccess);
        await store.SaveAsync(job);
        return job;
    }

    [Fact]
    public async Task Assignment_scope_survives_storage_leasing_and_progress_updates()
    {
        var store = CreateStore();
        var tenant = Guid.NewGuid().ToString();
        var client = Guid.NewGuid().ToString();
        var job = ImportJob.Create(Guid.NewGuid(), "scoped", "extract.csv", "imports/scoped.csv",
            "service", 1, Night1, tenant, client);
        Assert.True(await store.TryCreateAsync(job));
        var leased = (await store.LeaseNextPendingAsync(Night1))!;
        Assert.Equal(tenant, leased.TenantId);
        Assert.Equal(client, leased.ClientId);
        Assert.True(leased.RecordProgress(ImportJobCounters.Empty with { ProcessedRows = 1 }, Night1).IsSuccess);
        await store.SaveAsync(leased);
        Assert.Equal(leased.ToSnapshot(), (await store.GetAsync(job.Id))!.ToSnapshot());
        var listed = Assert.Single((await store.SearchAsync("scoped", PageRequest.Default)).Items);
        Assert.Equal(tenant, listed.TenantId);
        Assert.Equal(client, listed.ClientId);
    }

    [Fact]
    public async Task A_created_job_round_trips_every_field()
    {
        var store = CreateStore();
        var job = NewJob(totalRows: 42);

        Assert.True(await store.TryCreateAsync(job));

        Assert.Equal(job.ToSnapshot(), (await store.GetAsync(job.Id))!.ToSnapshot());
    }

    [Fact]
    public async Task An_empty_store_has_nothing_to_return_or_lease()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync(Guid.NewGuid()));
        Assert.Null(await store.GetActiveForGroupAsync(TestData.Group));
        Assert.Null(await store.LeaseNextPendingAsync(Night1));
        Assert.Empty(await store.FindStalledAsync(Night1, limit: 10));
        Assert.Equal(0, (await store.SearchAsync(groupId: null, PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task A_group_has_at_most_one_active_job()
    {
        var store = CreateStore();
        var first = NewJob();

        Assert.True(await store.TryCreateAsync(first));
        Assert.False(await store.TryCreateAsync(NewJob(at: Night1.AddMinutes(1))));
        Assert.True(await store.TryCreateAsync(NewJob("elsewhere")));

        Assert.Equal(first.Id, (await store.GetActiveForGroupAsync(TestData.Group))!.Id);
        Assert.Equal(2, (await store.SearchAsync(groupId: null, PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task A_processing_job_still_holds_its_group()
    {
        var store = CreateStore();
        Assert.True(await store.TryCreateAsync(NewJob()));
        var leased = await store.LeaseNextPendingAsync(Night1);

        Assert.False(await store.TryCreateAsync(NewJob(at: Night1.AddMinutes(1))));
        Assert.Equal(leased!.Id, (await store.GetActiveForGroupAsync(TestData.Group))!.Id);
    }

    [Theory]
    [InlineData(ImportJobStatus.Completed)]
    [InlineData(ImportJobStatus.Failed)]
    [InlineData(ImportJobStatus.Abandoned)]
    public async Task A_finished_job_frees_its_group(ImportJobStatus outcome)
    {
        var store = CreateStore();
        var job = NewJob();
        Assert.True(await store.TryCreateAsync(job));
        Assert.True(job.Start(Night1).IsSuccess);

        var finished = outcome switch
        {
            ImportJobStatus.Completed => job.Complete(Night1.AddMinutes(1)),
            ImportJobStatus.Failed => job.Fail("unreadable file", Night1.AddMinutes(1)),
            _ => job.Abandon("worker stopped reporting", Night1.AddMinutes(1))
        };
        Assert.True(finished.IsSuccess);
        await store.SaveAsync(job);

        Assert.Equal(job.ToSnapshot(), (await store.GetAsync(job.Id))!.ToSnapshot());
        Assert.Null(await store.GetActiveForGroupAsync(TestData.Group));
        Assert.True(await store.TryCreateAsync(NewJob(at: Night1.AddHours(1))));
    }

    [Fact]
    public async Task Leasing_claims_the_oldest_pending_job_and_never_the_same_one_twice()
    {
        var store = CreateStore();
        var older = NewJob("group-a", Night1);
        var newer = NewJob("group-b", Night1.AddMinutes(5));
        Assert.True(await store.TryCreateAsync(newer));
        Assert.True(await store.TryCreateAsync(older));

        var leaseAt = Night1.AddMinutes(10);
        var first = await store.LeaseNextPendingAsync(leaseAt);
        var second = await store.LeaseNextPendingAsync(leaseAt);

        Assert.Equal(older.Id, first!.Id);
        Assert.Equal(newer.Id, second!.Id);
        Assert.Null(await store.LeaseNextPendingAsync(leaseAt));

        var stored = (await store.GetAsync(older.Id))!;
        Assert.Equal(ImportJobStatus.Processing, stored.Status);
        Assert.Equal(leaseAt, stored.StartedAtUtc);
        Assert.Equal(leaseAt, stored.HeartbeatAtUtc);
        Assert.Equal(older.Version + 1, stored.Version);
        Assert.Equal(first.ToSnapshot(), stored.ToSnapshot());
    }

    [Fact]
    public async Task Progress_survives_a_save()
    {
        var store = CreateStore();
        var job = NewJob(totalRows: 100);
        Assert.True(await store.TryCreateAsync(job));

        var leased = (await store.LeaseNextPendingAsync(Night1))!;
        Assert.True(leased.RecordProgress(new ImportJobCounters(0, 40, 30, 5, 3, 2), Night1.AddMinutes(1)).IsSuccess);
        await store.SaveAsync(leased);

        var stored = (await store.GetAsync(job.Id))!;
        Assert.Equal(leased.ToSnapshot(), stored.ToSnapshot());
        Assert.Equal(40, stored.Counters.PercentComplete);
    }

    [Fact]
    public async Task Stalled_jobs_are_processing_jobs_whose_heartbeat_is_older_than_the_cutoff()
    {
        var store = CreateStore();
        var quiet = NewJob("group-a", Night1);
        var busy = NewJob("group-b", Night1.AddMinutes(1));
        var waiting = NewJob("group-c", Night1.AddMinutes(2));
        foreach (var job in new[] { quiet, busy, waiting })
        {
            Assert.True(await store.TryCreateAsync(job));
        }

        Assert.True(quiet.Start(Night1).IsSuccess);
        Assert.True(busy.Start(Night1).IsSuccess);
        Assert.True(busy.RecordProgress(ImportJobCounters.Empty, Night1.AddMinutes(30)).IsSuccess);
        await store.SaveAsync(quiet);
        await store.SaveAsync(busy);

        var stalled = await store.FindStalledAsync(Night1.AddMinutes(10), limit: 10);

        Assert.Equal(quiet.Id, Assert.Single(stalled).Id);
    }

    [Fact]
    public async Task Stalled_jobs_come_quietest_first_up_to_the_limit()
    {
        var store = CreateStore();
        var quieter = NewJob("group-a", Night1);
        var quiet = NewJob("group-b", Night1);
        Assert.True(await store.TryCreateAsync(quieter));
        Assert.True(await store.TryCreateAsync(quiet));
        Assert.True(quiet.Start(Night1.AddMinutes(5)).IsSuccess);
        Assert.True(quieter.Start(Night1).IsSuccess);
        await store.SaveAsync(quiet);
        await store.SaveAsync(quieter);

        var cutoff = Night1.AddHours(1);

        Assert.Equal(quieter.Id, Assert.Single(await store.FindStalledAsync(cutoff, limit: 1)).Id);
        Assert.Equal(
            [quieter.Id, quiet.Id],
            (await store.FindStalledAsync(cutoff, limit: 10)).Select(j => j.Id).ToArray());
    }

    [Fact]
    public async Task Search_returns_newest_first_and_filters_by_group()
    {
        var store = CreateStore();
        var oldest = await SeedFinishedAsync(store, TestData.Group, Night1);
        var other = await SeedFinishedAsync(store, "elsewhere", Night1.AddHours(1));
        var newest = await SeedFinishedAsync(store, TestData.Group, Night1.AddHours(2));

        var all = await store.SearchAsync(groupId: null, PageRequest.Default);
        var mine = await store.SearchAsync(TestData.Group, PageRequest.Default);

        Assert.Equal([newest.Id, other.Id, oldest.Id], all.Items.Select(j => j.Id).ToArray());
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([newest.Id, oldest.Id], mine.Items.Select(j => j.Id).ToArray());
        Assert.Equal(2, mine.TotalCount);
    }

    [Fact]
    public async Task Search_pages_without_repeating_or_skipping()
    {
        var store = CreateStore();
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            seeded.Add((await SeedFinishedAsync(store, TestData.Group, Night1.AddHours(i))).Id);
        }

        var first = await store.SearchAsync(TestData.Group, new PageRequest(1, 2));
        var second = await store.SearchAsync(TestData.Group, new PageRequest(2, 2));
        var third = await store.SearchAsync(TestData.Group, new PageRequest(3, 2));

        Assert.Equal(5, first.TotalCount);
        seeded.Reverse();
        Assert.Equal(seeded, first.Items.Concat(second.Items).Concat(third.Items).Select(j => j.Id));
    }

    [Fact]
    public async Task Rejections_come_back_for_their_own_job_in_row_order()
    {
        var store = CreateStore();
        var job = NewJob("group-a");
        var other = NewJob("group-b");
        Assert.True(await store.TryCreateAsync(job));
        Assert.True(await store.TryCreateAsync(other));

        var notFound = new ImportJobRejection(
            job.Id, 2, "2-ABC", ImportRejectionReasons.IssueNotFound, "No issue matches this CPH and rule.");
        await store.AddRejectionsAsync(
        [
            new ImportJobRejection(job.Id, 7, null, ImportRejectionReasons.InvalidRow, "CPH is missing."),
            notFound,
            new ImportJobRejection(other.Id, 1, null, ImportRejectionReasons.InvalidRow, "Not this job's."),
            new ImportJobRejection(job.Id, 4, null, ImportRejectionReasons.InvalidRow, "Date Received is not a date.")
        ]);
        await store.AddRejectionsAsync([]);

        var page = await store.GetRejectionsAsync(job.Id, PageRequest.Default);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal([2L, 4L, 7L], page.Items.Select(r => r.RowNumber).ToArray());
        Assert.Equal(notFound, page.Items[0]);
    }

    [Fact]
    public async Task Rejections_page_without_repeating_or_skipping()
    {
        var store = CreateStore();
        var job = NewJob();
        Assert.True(await store.TryCreateAsync(job));
        await store.AddRejectionsAsync(
            [.. Enumerable.Range(1, 5).Select(row => new ImportJobRejection(
                job.Id, row, null, ImportRejectionReasons.InvalidRow, $"row {row}"))]);

        var first = await store.GetRejectionsAsync(job.Id, new PageRequest(1, 2));
        var second = await store.GetRejectionsAsync(job.Id, new PageRequest(2, 2));
        var third = await store.GetRejectionsAsync(job.Id, new PageRequest(3, 2));

        Assert.Equal(5, first.TotalCount);
        Assert.Equal(
            [1L, 2L, 3L, 4L, 5L],
            first.Items.Concat(second.Items).Concat(third.Items).Select(r => r.RowNumber).ToArray());
    }
}
