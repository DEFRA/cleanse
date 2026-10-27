using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Test.Storage;

public abstract class UserStoreContractTests
{
    protected abstract IUserStore CreateStore();
    protected abstract IIssueStore CreateIssueStore();
    protected abstract IDataResetStore CreateResetStore();

    protected static KnownUser Login(string subject = "subject", string client = "client", string tenant = "tenant") =>
        new(Guid.NewGuid(), "https://login.microsoftonline.com/tenant/v2.0", subject, tenant, "object", client,
            "Alex Example", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Assignment_filters_distinguish_stable_legacy_blank_and_unassigned_before_paging()
    {
        var user = await CreateStore().UpsertLoginAsync(Login());
        var stable = Support.TestData.NewIssue("filter-stable");
        stable.AssignKnownUser(user, DateTimeOffset.UnixEpoch);
        var legacy = Support.TestData.NewIssue("filter-legacy");
        legacy.AssignTo("Legacy Person", DateTimeOffset.UnixEpoch);
        var empty = Support.TestData.NewIssue("filter-empty");
        var whitespace = Issue.FromSnapshot(Support.TestData.NewIssue("filter-space").ToSnapshot() with
        {
            AssignedTo = " \t\r\n\v\f\u00a0\u2003"
        });
        var stableWithoutText = Issue.FromSnapshot(Support.TestData.NewIssue("filter-id-only").ToSnapshot() with
        {
            AssignedUserId = user.Id
        });
        var store = CreateIssueStore();
        await store.SaveBatchAsync([stable, legacy, empty, whitespace, stableWithoutText]);

        var known = await store.SearchAsync(new IssueFilter { AssignedUserId = user.Id, Page = new PageRequest(1, 1) });
        Assert.Equal(2, known.TotalCount);
        Assert.Single(known.Items);
        var unassigned = await store.SearchAsync(new IssueFilter { Unassigned = true });
        Assert.Equal(2, unassigned.TotalCount);
        Assert.All(unassigned.Items, issue => Assert.Null(issue.AssignedUserId));
        Assert.DoesNotContain(unassigned.Items, issue => issue.Id == legacy.Id);
        Assert.Equal(3, (await store.SearchAsync(new IssueFilter { Unassigned = false })).TotalCount);
        Assert.Empty((await store.SearchAsync(new IssueFilter { AssignedUserId = Guid.NewGuid() })).Items);
    }

    [Fact]
    public async Task Repeated_login_keeps_identity_and_first_timestamp_but_refreshes_profile()
    {
        var users = CreateStore();
        var first = await users.UpsertLoginAsync(Login());
        var later = await users.UpsertLoginAsync(Login() with
        {
            FullName = "Alex Renamed",
            ObjectId = "updated-object",
            FirstLoggedInAtUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            LastLoggedInAtUtc = DateTimeOffset.UnixEpoch.AddDays(1)
        });

        Assert.Equal(first.Id, later.Id);
        Assert.Equal(first.FirstLoggedInAtUtc, later.FirstLoggedInAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), later.LastLoggedInAtUtc);
        Assert.Equal("Alex Renamed", later.FullName);
        Assert.Equal("updated-object", later.ObjectId);
        var stale = await users.UpsertLoginAsync(Login());
        Assert.Equal(later.LastLoggedInAtUtc, stale.LastLoggedInAtUtc);
        Assert.Equal(later.Id, stale.Id);
    }

    [Fact]
    public async Task Identity_key_is_exact_and_includes_issuer_client_and_subject()
    {
        var users = CreateStore();
        var first = await users.UpsertLoginAsync(Login());
        var issuer = await users.UpsertLoginAsync(Login() with { Issuer = "https://other.example.test" });
        var client = await users.UpsertLoginAsync(Login(client: "other-client"));
        var subject = await users.UpsertLoginAsync(Login("Subject"));

        Assert.Equal(4, new[] { first.Id, issuer.Id, client.Id, subject.Id }.Distinct().Count());
        Assert.Equal(3, (await users.SearchAsync("tenant", "client", PageRequest.Default)).TotalCount);
        Assert.Null(await users.GetAsync(first.Id, "wrong-tenant", "client"));
        Assert.Null(await users.GetAsync(first.Id, "tenant", "other-client"));
        Assert.Equal(first, await users.GetAsync(first.Id, "tenant", "client"));
    }

    [Fact]
    public async Task Directory_is_scoped_and_paged_without_duplicates()
    {
        var users = CreateStore();
        for (var i = 0; i < 5; i++)
        {
            await users.UpsertLoginAsync(Login($"s{i}"));
        }
        await users.UpsertLoginAsync(Login("other", tenant: "other-tenant"));
        await users.UpsertLoginAsync(Login("other", client: "other-client"));

        var first = await users.SearchAsync("tenant", "client", new PageRequest(1, 2));
        var second = await users.SearchAsync("tenant", "client", new PageRequest(2, 2));
        var third = await users.SearchAsync("tenant", "client", new PageRequest(3, 2));
        Assert.Equal(5, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, second.PageNumber);
        Assert.Equal(5, first.Items.Concat(second.Items).Concat(third.Items).Select(u => u.Id).Distinct().Count());
        Assert.Empty((await users.SearchAsync("tenant", "client", new PageRequest(4, 2))).Items);
    }

    [Fact]
    public async Task Concurrent_first_logins_converge_to_one_id()
    {
        var stores = Enumerable.Range(0, 12).Select(_ => CreateStore()).ToArray();
        var tasks = stores.Select(store => Task.Run(
            () => store.UpsertLoginAsync(Login()))).ToArray();
        var users = await Task.WhenAll(tasks);
        Assert.Single(users.Select(u => u.Id).Distinct());
        Assert.Equal(1, (await CreateStore().SearchAsync("tenant", "client", PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task Stable_assignment_round_trips_single_and_batch_saves_and_survives_detection()
    {
        var user = await CreateStore().UpsertLoginAsync(Login());
        var issues = CreateIssueStore();
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var issue = Issue.FromDetection(new IssueDetection(IssueId.From("rule:user-test"), "rule"), "g", "r", now);
        issue.AssignKnownUser(user, now);
        Assert.True(await issues.SaveAsync(issue, null));
        var reloaded = await issues.GetAsync(issue.Id);
        Assert.Equal(user.Id, reloaded!.AssignedUserId);
        Assert.Equal(user.FullName, reloaded.AssignedUserFullName);
        reloaded.RecordDetection("r2", IssueContext.Empty, now);

        // More than the PostgreSQL harness COPY threshold, to cover both batch write shapes.
        var batch = Enumerable.Range(0, 5).Select(i =>
        {
            var item = Issue.FromDetection(
                new IssueDetection(IssueId.From($"rule:user-{i}"), "rule"), "g", "r2", now);
            item.AssignKnownUser(user, now);
            return item;
        }).Append(reloaded).ToArray();
        await issues.SaveBatchAsync(batch);
        var page = await issues.SearchAsync(new IssueFilter { GroupId = "g" });
        Assert.All(page.Items, i => Assert.Equal(user.Id, i.AssignedUserId));
        Assert.Equal(6, page.TotalCount);
        foreach (var item in batch)
        {
            item.AssignTo("legacy-name", now);
        }
        await issues.SaveBatchAsync(batch);
        Assert.All((await issues.GetManyAsync(batch.Select(i => i.Id).ToArray())),
            i => Assert.Null(i.AssignedUserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("g")]
    public async Task Issue_resets_do_not_delete_known_users(string? group)
    {
        var users = CreateStore();
        var user = await users.UpsertLoginAsync(Login());
        var issue = Issue.FromDetection(new IssueDetection(IssueId.From("rule:reset-user"), "rule"),
            "g", "run", DateTimeOffset.UnixEpoch);
        issue.AssignKnownUser(user, DateTimeOffset.UnixEpoch);
        await CreateIssueStore().SaveAsync(issue, null);
        await CreateResetStore().ResetAsync(group, "test", DateTimeOffset.UnixEpoch);
        Assert.Equal(user, await users.GetAsync(user.Id, "tenant", "client"));
        Assert.Null(await CreateIssueStore().GetAsync(issue.Id));
    }
}
