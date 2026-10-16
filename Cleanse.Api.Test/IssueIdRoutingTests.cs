using Cleanse.Api.Test.Support;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

/// <summary>
/// Issue ids are derived from inputs that contain characters illegal in a URL — a CPH's slashes
/// above all. This pins that a derived id is addressable verbatim, because every single-issue
/// route depends on it.
/// </summary>
public class IssueIdRoutingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_derived_id_is_addressable_without_escaping()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"route-{Guid.NewGuid():N}";

        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        var id = IssueId.Derive("1", "01/036/8000").Value;
        Assert.Equal(id, Uri.EscapeDataString(id));

        await client.PostJsonAsync($"/v1/runs/{runId}/detections", new
        {
            detections = new[] { new { id, ruleId = "1", context = new Dictionary<string, string>() } }
        });

        var response = await client.GetAsync($"/v1/issues/{id}");

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET /v1/issues/{id} returned {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());

        var sub = await client.GetAsync($"/v1/issues/{id}/history");
        Assert.True(
            sub.IsSuccessStatusCode,
            $"GET history returned {(int)sub.StatusCode}: {await sub.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_detection_id_that_is_not_url_safe_is_refused()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"route-{Guid.NewGuid():N}";

        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        var response = await client.PostJsonAsync($"/v1/runs/{runId}/detections", new
        {
            detections = new[]
            {
                new { id = "1:01/036/8000", ruleId = "1", context = new Dictionary<string, string>() }
            }
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
