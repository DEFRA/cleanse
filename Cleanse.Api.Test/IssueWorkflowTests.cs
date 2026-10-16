using System.Net;
using System.Text.Json;
using Cleanse.Api.Test.Support;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

/// <summary>
/// A full nightly cycle driven entirely over HTTP, plus the failure modes the API tier owns:
/// status mapping, the batch ceiling, and query-string validation.
/// </summary>
public class IssueWorkflowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string Group = $"grp-{Guid.NewGuid():N}";

    private static string Id(string cph, string ruleId = "rule2") => IssueId.Derive(ruleId, cph).Value;

    private static object Detection(string cph, string ruleId = "rule2") => new
    {
        id = Id(cph, ruleId),
        ruleId,
        context = new Dictionary<string, string> { ["cph"] = cph }
    };

    [Fact]
    public async Task A_run_detects_triages_and_sweeps_over_http()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"{Group}-full";

        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        var upsert = await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections",
            new { detections = new[] { Detection("a"), Detection("b"), Detection("c") } });
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        Assert.Equal(3, (await upsert.ReadJsonAsync()).GetProperty("created").GetInt32());

        var complete = await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var issue = await client.GetAsync($"/v1/issues/{Id("a")}");
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        var body = await issue.ReadJsonAsync();
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.Equal("todo", body.GetProperty("workflowStatus").GetString());

        // A caseworker triages it.
        var assign = await client.PutJsonAsync($"/v1/issues/{Id("a")}/assignee", new { assignTo = "alice" });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        Assert.Equal("alice", (await assign.ReadJsonAsync()).GetProperty("assignedTo").GetString());

        var resolve = await client.PutJsonAsync(
            $"/v1/issues/{Id("a")}/workflow-status", new { workflowStatus = "resolved" });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);

        // A later run no longer reports b and c, so the sweep closes them.
        var second = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var secondRunId = (await second.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync(
            $"/v1/runs/{secondRunId}/detections", new { detections = new[] { Detection("a") } });
        var secondComplete = await client.PostJsonAsync($"/v1/runs/{secondRunId}/complete", new { });

        Assert.Equal(2, (await secondComplete.ReadJsonAsync()).GetProperty("issuesDeactivated").GetInt32());

        // Re-detection reactivates without disturbing the human decision.
        var reDetected = await (await client.GetAsync($"/v1/issues/{Id("a")}")).ReadJsonAsync();
        Assert.Equal("active", reDetected.GetProperty("status").GetString());
        Assert.Equal("resolved", reDetected.GetProperty("workflowStatus").GetString());
        Assert.Equal("alice", reDetected.GetProperty("assignedTo").GetString());
    }

    [Fact]
    public async Task The_audit_trail_records_the_authenticated_caller()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"{Group}-actor";

        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = new[] { Detection("actor", "rule9") } });

        await client.PutJsonAsync($"/v1/issues/{Id("actor", "rule9")}/assignee", new { assignTo = "bob" });

        var audit = await (await client.GetAsync(
            $"/v1/issues/{Id("actor", "rule9")}/audit")).ReadJsonAsync();
        var entries = audit.GetProperty("items").EnumerateArray().ToList();
        var assigned = entries.First(e => e.GetProperty("kind").GetString() == "IssueAssigned");

        // Taken from the credential, never from the request body.
        Assert.Equal("test-client", assigned.GetProperty("actorId").GetString());
    }

    [Fact]
    public async Task A_missing_issue_maps_to_404()
    {
        var response = await factory.CreateAuthenticatedClient().GetAsync("/v1/issues/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("issue.not_found", (await response.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_illegal_workflow_transition_maps_to_400()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"{Group}-transition";

        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = new[] { Detection("t", "rule7") } });

        await client.PutJsonAsync(
            $"/v1/issues/{Id("t", "rule7")}/workflow-status", new { workflowStatus = "ignored" });
        var invalid = await client.PutJsonAsync(
            $"/v1/issues/{Id("t", "rule7")}/workflow-status", new { workflowStatus = "resolved" });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(
            "issue.invalid_transition",
            (await invalid.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_stale_expected_version_maps_to_409()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"{Group}-concurrency";

        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;
        await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections", new { detections = new[] { Detection("c", "rule8") } });

        var original = await (await client.GetAsync($"/v1/issues/{Id("c", "rule8")}")).ReadJsonAsync();
        var version = original.GetProperty("version").GetInt32();

        await client.PutJsonAsync(
            $"/v1/issues/{Id("c", "rule8")}/assignee",
            new { assignTo = "first", expectedVersion = version });
        var stale = await client.PutJsonAsync(
            $"/v1/issues/{Id("c", "rule8")}/notes", new { notes = "second", expectedVersion = version });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "issue.concurrency_conflict",
            (await stale.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_second_run_for_a_group_maps_to_409()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"{Group}-inflight";

        await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var second = await client.PostJsonAsync("/v1/runs", new { groupId = group });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            "run.already_in_progress",
            (await second.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_workflow_status_is_rejected_by_name()
    {
        var response = await factory.CreateAuthenticatedClient()
            .PutJsonAsync("/v1/issues/anything/workflow-status", new { workflowStatus = "nonsense" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nonsense", (await response.ReadJsonAsync()).GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task An_unknown_search_status_is_rejected_rather_than_ignored()
    {
        var response = await factory.CreateAuthenticatedClient().GetAsync("/v1/issues?status=banana");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
