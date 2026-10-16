using System.Net;
using Cleanse.Api.Test.Support;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

/// <summary>
/// The bulk path is the one endpoint that can plausibly exceed the API Gateway's 29 second
/// ceiling, so its limit and its refusal behaviour are pinned here.
/// </summary>
public class BulkLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Issue ids are global, so each test needs its own prefix: the same id appearing under two
    // groups is a genuine conflict and Core rejects it.
    private static object[] Detections(int count, string prefix) =>
        [.. Enumerable.Range(0, count).Select(i => new
        {
            id = IssueId.Derive("rule2", $"{prefix}-{i:D5}").Value,
            ruleId = "rule2",
            context = new Dictionary<string, string> { ["cph"] = $"{prefix}-{i:D5}" }
        })];

    private async Task<(HttpClient Client, string RunId)> StartRunAsync(string suffix)
    {
        var client = factory.CreateAuthenticatedClient();
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = $"bulk-{suffix}-{Guid.NewGuid():N}" });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
        return (client, runId);
    }

    [Fact]
    public async Task A_batch_over_the_ceiling_is_refused_with_the_limit_in_the_body()
    {
        var (client, runId) = await StartRunAsync("over");

        var response = await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = Detections(1001, "over") });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var body = await response.ReadJsonAsync();
        Assert.Equal("detections.batch_too_large", body.GetProperty("code").GetString());
        Assert.Equal(1000, body.GetProperty("limit").GetInt32());
        Assert.Equal(1001, body.GetProperty("supplied").GetInt32());
    }

    [Fact]
    public async Task A_batch_exactly_at_the_ceiling_is_accepted()
    {
        var (client, runId) = await StartRunAsync("at");

        var response = await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = Detections(1000, "at") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1000, (await response.ReadJsonAsync()).GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task Nothing_is_written_when_a_batch_is_refused()
    {
        var (client, runId) = await StartRunAsync("atomic");

        await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = Detections(1001, "atomic") });

        var run = await (await client.GetAsync($"/v1/runs/{runId}")).ReadJsonAsync();
        Assert.Equal(0, run.GetProperty("detectionsProcessed").GetInt32());
    }

    [Fact]
    public async Task Detections_against_a_closed_run_are_refused()
    {
        var (client, runId) = await StartRunAsync("closed");
        await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });

        var response = await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = Detections(1, "closed") });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("run.not_running", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_abandoned_run_does_not_sweep()
    {
        var (client, runId) = await StartRunAsync("abandon");
        await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = Detections(3, "abandon") });

        var response = await client.PostJsonAsync(
            $"/v1/runs/{runId}/abandon", new { reason = "source system timed out" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("abandoned", body.GetProperty("state").GetString());
        Assert.Equal(0, body.GetProperty("issuesDeactivated").GetInt32());
    }

    [Fact]
    public async Task The_same_issue_id_cannot_be_claimed_by_a_second_group()
    {
        var (client, firstRun) = await StartRunAsync("owner");
        await client.PostJsonAsync(
            $"/v1/runs/{firstRun}/detections", new { detections = Detections(1, "contested") });

        var (_, secondRun) = await StartRunAsync("intruder");
        var response = await client.PostJsonAsync(
            $"/v1/runs/{secondRun}/detections", new { detections = Detections(1, "contested") });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "issue.group_mismatch",
            (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }
}
