using System.Net;
using System.Text.Json;
using Cleanse.Api.Test.Support;

namespace Cleanse.Api.Test;

/// <summary>
/// The operator's status surface. Its whole value is being answerable when something else is
/// not, so the contract under test is as much "does not fail" as "reports the right thing".
/// </summary>
public class DiagnosticsEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Diagnostics_needs_authentication()
    {
        var response = await factory.CreateClient().GetAsync("/v1/diagnostics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_reports_the_storage_provider_in_use()
    {
        var body = await GetAsync();

        var database = body.GetProperty("database");
        Assert.Equal("InMemory", database.GetProperty("provider").GetString());
        Assert.Equal("ok", database.GetProperty("state").GetString());
    }

    /// <summary>The test host runs as Development, where uploads go to a local directory.</summary>
    [Fact]
    public async Task Diagnostics_reports_where_import_files_are_kept()
    {
        var storage = (await GetAsync()).GetProperty("storage");

        Assert.Equal("FileSystem", storage.GetProperty("provider").GetString());
        Assert.Equal("ok", storage.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(storage.GetProperty("target").GetString()));
    }

    [Fact]
    public async Task Diagnostics_says_which_build_is_answering()
    {
        var service = (await GetAsync()).GetProperty("service");

        Assert.Equal("cleanse", service.GetProperty("name").GetString());
        Assert.Equal(ApiFactory.ServiceVersion, service.GetProperty("version").GetString());
        Assert.Equal(ApiFactory.CdpEnvironment, service.GetProperty("environment").GetString());
        Assert.StartsWith(".NET", service.GetProperty("runtime").GetString(), StringComparison.Ordinal);
        Assert.True(service.GetProperty("startedAtUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The test host points KRDS at a snapshot that is not there, which is exactly the case that
    /// must be reported with a reason rather than thrown or quietly claimed as healthy.
    /// </summary>
    [Fact]
    public async Task A_broken_dependency_is_reported_rather_than_thrown()
    {
        var krds = (await GetAsync()).GetProperty("krds");

        Assert.Equal("failed", krds.GetProperty("state").GetString());
        Assert.Contains("does not exist", krds.GetProperty("detail").GetString());
        Assert.Equal("LocalFile", krds.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task A_broken_dependency_does_not_stop_the_rest_being_reported()
    {
        var body = await GetAsync();

        // The KRDS probe above is failing, yet everything else still answers.
        Assert.Equal("ok", body.GetProperty("database").GetProperty("state").GetString());
        Assert.Equal("ok", body.GetProperty("storage").GetProperty("state").GetString());
        Assert.Equal("ok", body.GetProperty("issues").GetProperty("state").GetString());
    }

    /// <summary>
    /// Whether KRDS can be read, as a rule run reads it, is its own row: a source can answer and
    /// still refuse this service. The test host's snapshot is missing, so it cannot be read either.
    /// </summary>
    [Fact]
    public async Task Read_access_to_krds_is_reported_alongside_its_health()
    {
        var access = (await GetAsync()).GetProperty("krdsAccess");

        Assert.Equal("failed", access.GetProperty("state").GetString());
        Assert.Contains("does not exist", access.GetProperty("detail").GetString());
        Assert.Equal("LocalFile", access.GetProperty("provider").GetString());
        Assert.EndsWith("cleanse-no-such-snapshot.sqlite", access.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Issue_counts_are_broken_down_by_both_axes()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"grp-{Guid.NewGuid():N}";
        await SeedIssueAsync(client, group);

        var counts = (await GetAsync($"?groupId={group}")).GetProperty("issues");

        Assert.Equal("ok", counts.GetProperty("state").GetString());
        Assert.Equal(1, counts.GetProperty("total").GetInt32());

        var row = Assert.Single(counts.GetProperty("breakdown").EnumerateArray().ToArray());
        Assert.Equal("active", row.GetProperty("status").GetString());
        Assert.Equal("todo", row.GetProperty("workflowStatus").GetString());
        Assert.Equal(1, row.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Counts_span_every_group_when_none_is_named()
    {
        var client = factory.CreateAuthenticatedClient();
        await SeedIssueAsync(client, $"grp-{Guid.NewGuid():N}");
        await SeedIssueAsync(client, $"grp-{Guid.NewGuid():N}");

        var total = (await GetAsync()).GetProperty("issues").GetProperty("total").GetInt32();

        Assert.True(total >= 2, $"expected every group to be counted, got {total}");
    }

    [Fact]
    public async Task The_last_reset_is_reported_and_survives_the_reset_itself()
    {
        var client = factory.CreateResetClient();
        await client.PostJsonAsync("/v1/maintenance/reset", new { });

        var lastReset = (await GetAsync()).GetProperty("lastResetAtUtc");

        Assert.NotEqual(JsonValueKind.Null, lastReset.ValueKind);
    }

    private async Task<JsonElement> GetAsync(string query = "")
    {
        var response = await factory.CreateAuthenticatedClient().GetAsync($"/v1/diagnostics{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static async Task SeedIssueAsync(HttpClient client, string group)
    {
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        await client.PostJsonAsync(
            $"/v1/runs/{runId}/detections",
            new { detections = new[] { new { id = $"1-{Guid.NewGuid():N}", ruleId = "1" } } });

        await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });
    }
}
