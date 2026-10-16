using System.Net;
using Cleanse.Api.Test.Support;

namespace Cleanse.Api.Test;

/// <summary>
/// The rules engine's HTTP surface: the rule book, the run history, and the adhoc trigger.
/// </summary>
/// <remarks>
/// The test host leaves <c>Rules:Enabled</c> false, so no background worker is running. Run
/// concurrency is therefore exercised through <c>POST /v1/runs</c>, which needs no engine, and
/// the trigger is pinned to refuse rather than to open a run nothing would ever execute.
/// </remarks>
public class RuleEngineEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_rule_book_lists_every_rule_in_execution_order()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/v1/rules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rules = (await response.ReadJsonAsync()).EnumerateArray().ToArray();
        Assert.Equal(2, rules.Length);

        Assert.Equal("2", rules[0].GetProperty("ruleNumber").GetString());
        Assert.Equal("02", rules[0].GetProperty("errorCode").GetString());
        Assert.Equal("CTS-CPH-missing-in-Sam", rules[0].GetProperty("errorDescription").GetString());
        Assert.Equal("active", rules[0].GetProperty("status").GetString());

        Assert.Equal("1", rules[1].GetProperty("ruleNumber").GetString());
        Assert.Equal("01", rules[1].GetProperty("errorCode").GetString());
        Assert.Equal("No-cattle-unit-in-Sam", rules[1].GetProperty("errorDescription").GetString());
    }

    [Fact]
    public async Task The_rule_book_needs_authentication()
    {
        var response = await factory.CreateClient().GetAsync("/v1/rules");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Run_history_is_paginated_and_filterable_by_group()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"grp-{Guid.NewGuid():N}";

        foreach (var _ in Enumerable.Range(0, 3))
        {
            var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
            var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
            await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });
        }

        var page = await client.GetAsync($"/v1/runs?groupId={group}&pageNumber=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var body = await page.ReadJsonAsync();
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());

        var first = body.GetProperty("items")[0];
        Assert.Equal(group, first.GetProperty("groupId").GetString());
        Assert.Equal("completed", first.GetProperty("state").GetString());
        Assert.Equal(100d, first.GetProperty("progressPercent").GetDouble());
    }

    [Fact]
    public async Task Run_history_can_be_filtered_by_state()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"grp-{Guid.NewGuid():N}";

        var completed = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var completedId = (await completed.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync($"/v1/runs/{completedId}/complete", new { });

        var abandoned = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var abandonedId = (await abandoned.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync($"/v1/runs/{abandonedId}/abandon", new { reason = "gave up" });

        var page = await client.GetAsync($"/v1/runs?groupId={group}&state=abandoned");

        var items = (await page.ReadJsonAsync()).GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(abandonedId, items[0].GetProperty("runId").GetString());
    }

    [Fact]
    public async Task An_unknown_run_state_is_rejected_by_name()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/v1/runs?state=exploded");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("exploded", (await response.ReadJsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Triggering_is_refused_when_the_engine_is_disabled()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostJsonAsync("/v1/runs/trigger", new { });

        // Not 202. Nothing would drain the queue, so an accepted run would sit in Running
        // forever and hold the group's only slot.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.ReadJsonAsync();
        Assert.Equal("rules.engine_disabled", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_group_may_only_have_one_run_in_flight()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"grp-{Guid.NewGuid():N}";

        var first = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var body = await first.ReadJsonAsync();
        var runId = body.GetProperty("runId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(runId));
        Assert.Equal("running", body.GetProperty("state").GetString());

        var second = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var cancelled = await client.PostJsonAsync($"/v1/runs/{runId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.True((await cancelled.ReadJsonAsync()).GetProperty("cancellationRequested").GetBoolean());

        // Cancellation is a request, not an instruction: the run is still running until the
        // engine notices, so it still holds the group's slot.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostJsonAsync("/v1/runs", new { groupId = group })).StatusCode);

        await client.PostJsonAsync($"/v1/runs/{runId}/abandon", new { reason = "test cleanup" });

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostJsonAsync("/v1/runs", new { groupId = group })).StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_unknown_run_is_a_404()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostJsonAsync("/v1/runs/does-not-exist/cancel", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_run_exposes_its_progress_and_diagnostics()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"grp-{Guid.NewGuid():N}";

        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        var run = await (await client.GetAsync($"/v1/runs/{runId}")).ReadJsonAsync();

        Assert.Equal(0, run.GetProperty("recordsProcessed").GetInt32());
        Assert.Equal(0, run.GetProperty("recordsTotal").GetInt32());
        Assert.Equal(0d, run.GetProperty("progressPercent").GetDouble());
        Assert.False(run.GetProperty("cancellationRequested").GetBoolean());
        Assert.True(run.TryGetProperty("heartbeatAtUtc", out _));
        Assert.True(run.TryGetProperty("estimatedCompletionAtUtc", out _));
        Assert.True(run.TryGetProperty("failureDetail", out _));

        await client.PostJsonAsync($"/v1/runs/{runId}/abandon", new { reason = "test cleanup" });
    }
}
