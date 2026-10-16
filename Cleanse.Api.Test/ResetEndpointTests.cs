using System.Net;
using System.Text.Json;
using Cleanse.Api.Test.Support;
using Cleanse.Auth;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

internal static class ResetSeed
{
    /// <summary>
    /// Drives a complete run so issues, a rule run, audit entries and case history all exist.
    /// Issue ids are derived from rule and cph, so each group needs its own cph: re-detecting an
    /// id that belongs to another group is a group mismatch, not a move.
    /// </summary>
    public static async Task<string> RunAsync(HttpClient client, string group, string cphPrefix)
    {
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections",
            new { detections = new[] { Detection($"{cphPrefix}-a"), Detection($"{cphPrefix}-b") } });
        await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });

        var first = Id($"{cphPrefix}-a");
        await client.PutJsonAsync($"/v1/issues/{first}/assignee", new { assignTo = "alice" });
        await client.PostJsonAsync(
            $"/v1/issues/{first}/history",
            new { actionType = "PhoneCall", comments = "chased the keeper" });

        return runId;
    }

    public static async Task<int> CountIssuesAsync(HttpClient client, string group) =>
        (await (await client.GetAsync($"/v1/issues?groupId={group}")).ReadJsonAsync())
        .GetProperty("totalCount").GetInt32();

    public static string Id(string cph) => IssueId.Derive("rule2", cph).Value;

    private static object Detection(string cph) => new
    {
        id = Id(cph),
        ruleId = "rule2",
        context = new Dictionary<string, string> { ["cph"] = cph }
    };
}

/// <summary>
/// Owns its own host, and so its own storage, because it clears the lot. Sharing a factory with
/// the guard tests would make the counts depend on execution order.
/// </summary>
public class GlobalResetTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_reset_deletes_the_issues_and_everything_recorded_against_them()
    {
        var client = factory.CreateResetClient();
        var group = $"reset-{Guid.NewGuid():N}";
        var runId = await ResetSeed.RunAsync(client, group, "wipe");

        Assert.Equal(2, await ResetSeed.CountIssuesAsync(client, group));

        var reset = await client.PostJsonAsync("/v1/maintenance/reset", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var counts = await reset.ReadJsonAsync();
        Assert.Equal("all", counts.GetProperty("scope").GetString());
        Assert.Equal(2, counts.GetProperty("issues").GetInt32());
        Assert.Equal(1, counts.GetProperty("issueHistoryEntries").GetInt32());
        Assert.Equal(1, counts.GetProperty("ruleRuns").GetInt32());
        Assert.True(counts.GetProperty("auditEntries").GetInt32() > 0);

        // A proper delete, not the end-of-run deactivation: the rows are gone, not just inactive.
        Assert.Equal(0, await ResetSeed.CountIssuesAsync(client, group));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/v1/issues/rule2:wipe-a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/runs/{runId}")).StatusCode);
    }

    [Fact]
    public async Task A_reset_that_matches_nothing_is_a_no_op_rather_than_an_error()
    {
        var reset = await factory.CreateResetClient()
            .PostJsonAsync("/v1/maintenance/reset", new { groupId = "no-such-group" });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(0, (await reset.ReadJsonAsync()).GetProperty("issues").GetInt32());
    }
}

public class ResetScopeAndGuardTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_group_scoped_reset_leaves_the_other_groups_standing()
    {
        var client = factory.CreateResetClient();
        var doomed = $"doomed-{Guid.NewGuid():N}";
        var spared = $"spared-{Guid.NewGuid():N}";

        await ResetSeed.RunAsync(client, doomed, "doomed");
        var sparedRunId = await ResetSeed.RunAsync(client, spared, "spared");

        var reset = await client.PostJsonAsync("/v1/maintenance/reset", new { groupId = doomed });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var counts = await reset.ReadJsonAsync();
        Assert.Equal(doomed, counts.GetProperty("scope").GetString());
        Assert.Equal(2, counts.GetProperty("issues").GetInt32());
        Assert.Equal(1, counts.GetProperty("issueHistoryEntries").GetInt32());
        Assert.Equal(1, counts.GetProperty("ruleRuns").GetInt32());

        Assert.Equal(0, await ResetSeed.CountIssuesAsync(client, doomed));
        Assert.Equal(2, await ResetSeed.CountIssuesAsync(client, spared));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/runs/{sparedRunId}")).StatusCode);

        // History and audit are keyed by issue, not by group, so this is where a scoped delete
        // would most easily take too much.
        var sparedIssue = ResetSeed.Id("spared-a");

        var history = await (await client.GetAsync($"/v1/issues/{sparedIssue}/history")).ReadJsonAsync();
        Assert.Equal(1, history.GetProperty("totalCount").GetInt32());

        var audit = await (await client.GetAsync($"/v1/issues/{sparedIssue}/audit")).ReadJsonAsync();
        Assert.True(audit.GetProperty("totalCount").GetInt32() > 0);
    }

    [Fact]
    public async Task A_reset_without_the_reset_key_header_is_refused_and_changes_nothing()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"guard-missing-{Guid.NewGuid():N}";
        await ResetSeed.RunAsync(client, group, "missing");

        var reset = await client.PostJsonAsync("/v1/maintenance/reset", new { });

        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal("reset.key_required", (await reset.ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(2, await ResetSeed.CountIssuesAsync(client, group));
    }

    [Fact]
    public async Task A_reset_with_the_wrong_reset_key_is_refused_and_changes_nothing()
    {
        var client = factory.CreateResetClient("not-the-reset-key");
        var group = $"guard-wrong-{Guid.NewGuid():N}";
        await ResetSeed.RunAsync(client, group, "wrong");

        var reset = await client.PostJsonAsync("/v1/maintenance/reset", new { });

        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal("reset.key_invalid", (await reset.ReadJsonAsync()).GetProperty("code").GetString());
        Assert.Equal(2, await ResetSeed.CountIssuesAsync(client, group));
    }

    [Fact]
    public async Task The_reset_key_alone_does_not_authenticate_a_caller()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ResetKeyOptions.HeaderName, ApiFactory.ResetKey);

        var reset = await client.PostJsonAsync("/v1/maintenance/reset", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, reset.StatusCode);
    }
}

public class ResetKeyOptionsTests
{
    [Fact]
    public void An_unset_key_turns_the_reset_off_entirely()
    {
        var options = new ResetKeyOptions();

        Assert.False(options.IsConfigured);
        Assert.False(options.Matches("anything"));
        Assert.False(options.Matches(string.Empty));
        Assert.False(options.Matches(null));
    }

    [Fact]
    public void A_whitespace_key_counts_as_unset()
    {
        var options = new ResetKeyOptions { Key = "   " };

        Assert.False(options.IsConfigured);
        Assert.False(options.Matches("   "));
    }

    [Theory]
    [InlineData("secret", true)]
    [InlineData("Secret", false)]
    [InlineData("secre", false)]
    [InlineData("secrets", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_configured_key_matches_only_itself(string? supplied, bool expected)
    {
        var options = new ResetKeyOptions { Key = "secret" };

        Assert.True(options.IsConfigured);
        Assert.Equal(expected, options.Matches(supplied));
    }
}
