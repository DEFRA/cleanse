using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cleanse.Api.Test.Support;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

/// <summary>
/// The import driven over HTTP, end to end: upload, background processing, progress, and the
/// reconciliation report. The processor runs for real, so these also exercise the lease.
/// </summary>
public class ImportEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Header =
        "Date Received,Priority,CPH,Rule No,Rule Name,Error Code,Error Code Description," +
        "Status,Assigned to,1st Contact Date,2nd Contact Date,3rd Contact Date," +
        "Letter/email sent,Notes,Date Complete";

    private static string Row(
        string cph, string ruleNo, string status, string notes = "", string priority = "4. Low",
        string first = "", string letter = "") =>
        $"2025-11-27T00:00:00Z,{priority},{cph},{ruleNo},Rule Name,1,Desc,{status},a@b.gov.uk," +
        $"{first},,,{letter},\"{notes}\",";

    private static MultipartFormDataContent Upload(string groupId, params string[] rows)
    {
        var csv = string.Join("\n", [Header, .. rows]);
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return new MultipartFormDataContent
        {
            { file, "file", "extract.csv" },
            { new StringContent(groupId), "groupId" }
        };
    }

    private async Task<string> SeedIssueAsync(HttpClient client, string group, string cph, string ruleNo)
    {
        var start = await client.PostJsonAsync("/v1/runs", new { groupId = group });
        var runId = (await start.ReadJsonAsync()).GetProperty("runId").GetString()!;

        // The import matches on the key the rules engine produces, so it is built the same way.
        var id = IssueId.Derive(ruleNo, cph).Value;
        await client.PostJsonAsync($"/v1/runs/{runId}/detections", new
        {
            detections = new[] { new { id, ruleId = ruleNo, context = new Dictionary<string, string>() } }
        });
        await client.PostJsonAsync($"/v1/runs/{runId}/complete", new { });
        return id;
    }

    private static async Task<System.Text.Json.JsonElement> WaitForCompletionAsync(
        HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await (await client.GetAsync($"/v1/imports/{jobId}")).ReadJsonAsync();
            var status = job.GetProperty("status").GetString();

            if (status is "completed" or "failed" or "abandoned")
            {
                return job;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Import {jobId} did not finish.");
    }

    [Fact]
    public async Task An_upload_is_accepted_and_processed_in_the_background()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";
        var issueId = await SeedIssueAsync(client, group, "01/036/8000", "1");

        var response = await client.PostAsync("/v1/imports", Upload(group,
            Row("01/036/8000", "1", "Completed", "archived on SAM. no longer on CTS NFA", "3. Medium",
                first: "2026-01-19T00:00:00Z")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = Guid.Parse((await response.ReadJsonAsync()).GetProperty("id").GetString()!);

        var finished = await WaitForCompletionAsync(client, jobId);
        Assert.Equal("completed", finished.GetProperty("status").GetString());
        Assert.Equal(100, finished.GetProperty("percentComplete").GetInt32());
        Assert.Equal(1, finished.GetProperty("matched").GetInt32());

        var issue = await (await client.GetAsync($"/v1/issues/{Uri.EscapeDataString(issueId)}")).ReadJsonAsync();
        Assert.Equal("resolved", issue.GetProperty("workflowStatus").GetString());
        Assert.Equal("medium", issue.GetProperty("priority").GetString());
        Assert.Equal("archived on SAM. no longer on CTS NFA", issue.GetProperty("externalNotes").GetString());

        var history = await (await client.GetAsync(
            $"/v1/issues/{Uri.EscapeDataString(issueId)}/history")).ReadJsonAsync();
        Assert.Equal(1, history.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Importing_the_same_extract_twice_changes_nothing_the_second_time()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";
        var issueId = await SeedIssueAsync(client, group, "02/092/8000", "1");
        var escaped = Uri.EscapeDataString(issueId);

        var rows = new[]
        {
            Row("02/092/8000", "1", "Completed", "same notes", first: "2026-01-19T00:00:00Z",
                letter: "2026-02-01T00:00:00Z")
        };

        var firstJob = Guid.Parse((await (await client.PostAsync("/v1/imports", Upload(group, rows)))
            .ReadJsonAsync()).GetProperty("id").GetString()!);
        await WaitForCompletionAsync(client, firstJob);

        var afterFirst = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();
        var historyAfterFirst = await (await client.GetAsync($"/v1/issues/{escaped}/history")).ReadJsonAsync();
        var auditAfterFirst = await (await client.GetAsync($"/v1/issues/{escaped}/audit")).ReadJsonAsync();

        var secondJob = Guid.Parse((await (await client.PostAsync("/v1/imports", Upload(group, rows)))
            .ReadJsonAsync()).GetProperty("id").GetString()!);
        await WaitForCompletionAsync(client, secondJob);

        var afterSecond = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();
        var historyAfterSecond = await (await client.GetAsync($"/v1/issues/{escaped}/history")).ReadJsonAsync();
        var auditAfterSecond = await (await client.GetAsync($"/v1/issues/{escaped}/audit")).ReadJsonAsync();

        // Deterministic history ids mean the second run upserts rather than inserting again.
        Assert.Equal(2, historyAfterFirst.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            historyAfterFirst.GetProperty("totalCount").GetInt32(),
            historyAfterSecond.GetProperty("totalCount").GetInt32());

        // Nothing material changed, so no second audit entry and no version bump.
        Assert.Equal(
            auditAfterFirst.GetProperty("totalCount").GetInt32(),
            auditAfterSecond.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            afterFirst.GetProperty("version").GetInt32(),
            afterSecond.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Rows_with_no_matching_issue_are_reported_not_created()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";

        var jobId = Guid.Parse((await (await client.PostAsync("/v1/imports",
            Upload(group, Row("99/999/9999", "1", "Completed")))).ReadJsonAsync())
            .GetProperty("id").GetString()!);

        var finished = await WaitForCompletionAsync(client, jobId);
        Assert.Equal(1, finished.GetProperty("unmatched").GetInt32());
        Assert.Equal(0, finished.GetProperty("matched").GetInt32());

        var rejections = await (await client.GetAsync($"/v1/imports/{jobId}/rejections")).ReadJsonAsync();
        var first = rejections.GetProperty("items")[0];
        Assert.Equal("issue_not_found", first.GetProperty("reason").GetString());

        // Nothing was invented from the CSV.
        var missing = await client.GetAsync(
            $"/v1/issues/{Uri.EscapeDataString($"{group}:1|99/999/9999")}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_import_never_touches_the_machine_owned_axis()
    {
        var client = factory.CreateAuthenticatedClient();
        var group = $"imp-{Guid.NewGuid():N}";
        var issueId = await SeedIssueAsync(client, group, "03/043/0203", "1");
        var escaped = Uri.EscapeDataString(issueId);

        var before = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();

        var jobId = Guid.Parse((await (await client.PostAsync("/v1/imports",
            Upload(group, Row("03/043/0203", "1", "Unable to Action")))).ReadJsonAsync())
            .GetProperty("id").GetString()!);
        await WaitForCompletionAsync(client, jobId);

        var after = await (await client.GetAsync($"/v1/issues/{escaped}")).ReadJsonAsync();

        Assert.Equal("nonactionable", after.GetProperty("workflowStatus").GetString());
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("runId").GetString(), after.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task A_file_that_is_not_the_expected_extract_is_rejected_before_storage()
    {
        var client = factory.CreateAuthenticatedClient();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("MZ\u0090\u0000not a csv at all"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var content = new MultipartFormDataContent
        {
            { file, "file", "payload.exe" },
            { new StringContent("cts-sam"), "groupId" }
        };

        var response = await client.PostAsync("/v1/imports", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_requires_authentication()
    {
        var response = await factory.CreateClient()
            .PostAsync("/v1/imports", Upload("cts-sam", Row("01/036/8000", "1", "Completed")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
