using System.Globalization;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Storage;

public abstract class AuditStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;

    protected abstract IAuditStore CreateStore();

    private static AuditEntry IssueEntry(string cph, DateTimeOffset at, string? runId = null) =>
        AuditEntry.ForIssue(
            TestData.Key(cph),
            new IssueChange(AuditEventKind.IssueAssigned, [new AuditChange("AssignedTo", null, "alice")]),
            TestData.Alice,
            at,
            runId);

    [Fact]
    public async Task Entries_are_returned_for_their_own_issue_only()
    {
        var store = CreateStore();
        await store.AppendAsync(IssueEntry("a", Night1));
        await store.AppendAsync(IssueEntry("b", Night1));

        var page = await store.GetForIssueAsync(TestData.Key("a"), PageRequest.Default);

        var entry = Assert.Single(page.Items);
        Assert.Equal(TestData.Key("a").Value, entry.IssueId!.Value.Value);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Entries_come_back_newest_first()
    {
        var store = CreateStore();
        await store.AppendAsync(IssueEntry("a", Night1));
        await store.AppendAsync(IssueEntry("a", Night1.AddDays(2)));
        await store.AppendAsync(IssueEntry("a", Night1.AddDays(1)));

        var page = await store.GetForIssueAsync(TestData.Key("a"), PageRequest.Default);

        Assert.Equal(
            [Night1.AddDays(2), Night1.AddDays(1), Night1],
            page.Items.Select(e => e.AtUtc).ToArray());
    }

    [Fact]
    public async Task Batch_append_preserves_every_entry()
    {
        var store = CreateStore();

        await store.AppendBatchAsync([IssueEntry("a", Night1), IssueEntry("a", Night1), IssueEntry("b", Night1)]);

        Assert.Equal(2, (await store.GetForIssueAsync(TestData.Key("a"), PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task Run_entries_are_queryable_by_run()
    {
        var store = CreateStore();
        await store.AppendAsync(AuditEntry.ForRun("run-1", AuditEventKind.RunStarted, Actor.System, Night1));
        await store.AppendAsync(AuditEntry.ForRun("run-1", AuditEventKind.RunCompleted, Actor.System, Night1));
        await store.AppendAsync(AuditEntry.ForRun("run-2", AuditEventKind.RunStarted, Actor.System, Night1));

        var page = await store.GetForRunAsync("run-1", PageRequest.Default);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, e => Assert.Null(e.IssueId));
    }

    [Fact]
    public async Task Issue_entries_tagged_with_a_run_appear_under_both()
    {
        var store = CreateStore();
        await store.AppendAsync(IssueEntry("a", Night1, runId: "run-1"));

        Assert.Equal(1, (await store.GetForIssueAsync(TestData.Key("a"), PageRequest.Default)).TotalCount);
        Assert.Equal(1, (await store.GetForRunAsync("run-1", PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task Paging_reports_the_unpaged_total()
    {
        var store = CreateStore();
        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(IssueEntry("a", Night1.AddMinutes(i)));
        }

        var page = await store.GetForIssueAsync(TestData.Key("a"), new PageRequest(2, 2));

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task An_issue_with_no_history_returns_an_empty_page()
    {
        var store = CreateStore();

        var page = await store.GetForIssueAsync(TestData.Key("nothing"), PageRequest.Default);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Entries_sharing_a_timestamp_page_without_repeating_or_skipping()
    {
        // A batch write stamps every entry with the same instant, so the tie-break has to be
        // deterministic or paging over them silently drops and duplicates rows.
        var store = CreateStore();
        for (var i = 0; i < 6; i++)
        {
            await store.AppendAsync(AuditEntry.ForIssue(
                TestData.Key("a"),
                new IssueChange(
                    AuditEventKind.IssueNotesChanged,
                    [new AuditChange("Notes", null, i.ToString(CultureInfo.InvariantCulture))]),
                TestData.Alice,
                Night1));
        }

        var first = await store.GetForIssueAsync(TestData.Key("a"), new PageRequest(1, 3));
        var second = await store.GetForIssueAsync(TestData.Key("a"), new PageRequest(2, 3));

        Assert.Equal(
            ["5", "4", "3", "2", "1", "0"],
            first.Items.Concat(second.Items).Select(e => e.Changes[0].To ?? string.Empty).ToArray());
    }
}

public abstract class IssueHistoryStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;

    protected abstract IIssueHistoryStore CreateStore();

    /// <summary>
    /// Providers that enforce referential integrity need the parent issue to exist before a
    /// history entry can reference it. In-memory has nothing to do; Postgres has a foreign key.
    /// </summary>
    protected virtual Task EnsureIssueExistsAsync(IssueId issueId) => Task.CompletedTask;

    private static IssueHistoryEntry Entry(string cph, HistoryActionType type, string comments, DateTimeOffset at) =>
        IssueHistoryEntry.Create(TestData.Key(cph), type, comments, TestData.Alice, at);

    [Fact]
    public async Task Added_entries_round_trip_by_id()
    {
        var store = CreateStore();
        await EnsureIssueExistsAsync(TestData.Key("a"));
        var entry = Entry("a", HistoryActionType.LetterSent, "posted first reminder", Night1);

        await store.AddAsync(entry);
        var loaded = await store.GetAsync(entry.Id);

        Assert.NotNull(loaded);
        Assert.Equal(entry.ToSnapshot(), loaded.ToSnapshot());
    }

    [Fact]
    public async Task An_unknown_id_returns_null()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Entries_are_scoped_to_their_issue_and_newest_first()
    {
        var store = CreateStore();
        await EnsureIssueExistsAsync(TestData.Key("a"));
        await EnsureIssueExistsAsync(TestData.Key("b"));
        await store.AddAsync(Entry("a", HistoryActionType.Comment, "first", Night1));
        await store.AddAsync(Entry("a", HistoryActionType.PhoneCall, "second", Night1.AddDays(1)));
        await store.AddAsync(Entry("b", HistoryActionType.Comment, "other issue", Night1));

        var page = await store.GetForIssueAsync(TestData.Key("a"), PageRequest.Default);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal("second", page.Items[0].Comments);
        Assert.Equal("first", page.Items[1].Comments);
    }

    [Fact]
    public async Task All_three_action_types_are_preserved()
    {
        var store = CreateStore();
        await EnsureIssueExistsAsync(TestData.Key("a"));
        foreach (var type in Enum.GetValues<HistoryActionType>())
        {
            await store.AddAsync(Entry("a", type, type.ToString(), Night1));
        }

        var page = await store.GetForIssueAsync(TestData.Key("a"), PageRequest.Default);

        Assert.Equivalent(
            Enum.GetValues<HistoryActionType>(),
            page.Items.Select(e => e.ActionType).ToArray());
    }

    [Fact]
    public async Task Entries_sharing_a_timestamp_page_without_repeating_or_skipping()
    {
        var store = CreateStore();
        await EnsureIssueExistsAsync(TestData.Key("a"));
        for (var i = 0; i < 6; i++)
        {
            await store.AddAsync(Entry(
                "a", HistoryActionType.Comment, i.ToString(CultureInfo.InvariantCulture), Night1));
        }

        var first = await store.GetForIssueAsync(TestData.Key("a"), new PageRequest(1, 3));
        var second = await store.GetForIssueAsync(TestData.Key("a"), new PageRequest(2, 3));

        Assert.Equal(
            ["5", "4", "3", "2", "1", "0"],
            first.Items.Concat(second.Items).Select(e => e.Comments).ToArray());
    }
}

public abstract class RuleRunStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;

    protected abstract IRuleRunStore CreateStore();

    [Fact]
    public async Task Save_then_get_round_trips()
    {
        var store = CreateStore();
        var run = RuleRun.Start("run-1", TestData.Group, Night1);

        await store.SaveAsync(run);

        Assert.Equal(run.ToSnapshot(), (await store.GetAsync("run-1"))!.ToSnapshot());
    }

    [Fact]
    public async Task An_unknown_run_returns_null()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync("nope"));
        Assert.Null(await store.GetActiveForGroupAsync(TestData.Group));
        Assert.Null(await store.GetLatestForGroupAsync(TestData.Group));
    }

    [Fact]
    public async Task Only_a_running_run_counts_as_active()
    {
        var store = CreateStore();
        var finished = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(finished.Complete(0, Night1.AddHours(1)).IsSuccess);
        await store.SaveAsync(finished);

        Assert.Null(await store.GetActiveForGroupAsync(TestData.Group));

        await store.SaveAsync(RuleRun.Start("run-2", TestData.Group, Night1.AddDays(1)));

        Assert.Equal("run-2", (await store.GetActiveForGroupAsync(TestData.Group))!.RunId);
    }

    [Fact]
    public async Task Active_and_latest_are_scoped_to_their_group()
    {
        var store = CreateStore();
        await store.SaveAsync(RuleRun.Start("mine", TestData.Group, Night1));
        await store.SaveAsync(RuleRun.Start("theirs", "elsewhere", Night1.AddDays(1)));

        Assert.Equal("mine", (await store.GetActiveForGroupAsync(TestData.Group))!.RunId);
        Assert.Equal("mine", (await store.GetLatestForGroupAsync(TestData.Group))!.RunId);
    }

    [Fact]
    public async Task Latest_is_the_most_recently_started_whatever_its_state()
    {
        var store = CreateStore();
        var first = RuleRun.Start("run-1", TestData.Group, Night1);
        Assert.True(first.Complete(0, Night1.AddHours(1)).IsSuccess);
        var second = RuleRun.Start("run-2", TestData.Group, Night1.AddDays(1));
        Assert.True(second.Abandon("boom", Night1.AddDays(1)).IsSuccess);

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        Assert.Equal("run-2", (await store.GetLatestForGroupAsync(TestData.Group))!.RunId);
    }

    [Fact]
    public async Task Saving_the_same_run_again_overwrites_it()
    {
        var store = CreateStore();
        var run = RuleRun.Start("run-1", TestData.Group, Night1);
        await store.SaveAsync(run);

        Assert.True(run.Complete(3, Night1.AddHours(1)).IsSuccess);
        await store.SaveAsync(run);

        var loaded = await store.GetAsync("run-1");
        Assert.Equal(RunState.Completed, loaded!.State);
        Assert.Equal(3, loaded.Counters.IssuesDeactivated);
    }
}
