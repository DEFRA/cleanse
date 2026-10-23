using System.Net;
using System.Text;
using Cleanse.Core.Abstractions;
using Cleanse.Rules.Krds;
using Microsoft.Data.Sqlite;

namespace Cleanse.Rules.Test;

/// <summary>
/// The two KRDS rows on the status page. The Data Bridge's health is checked through its own
/// anonymous /health endpoint; access is checked by reading the latest snapshot with the key, as
/// a run does. Only the bridge's host name is ever shown, and never the key or a download URL.
/// </summary>
public class KrdsDiagnosticsTests
{
    private const string Host = "ls-keeper-data-bridge-backend.ext-test.cdp-int.defra.cloud";
    private const string BaseUrl = $"https://{Host}";

    /// <summary>The shape the Data Bridge writes, as captured from ext-test.</summary>
    private static string HealthReport(string status, params (string Name, string Status)[] checks)
    {
        var results = string.Join(
            ",",
            (checks.Length == 0 ? [("mongodb", "Healthy"), ("aws_s3", "Healthy")] : checks)
                .Select(check => $$"""
                    "{{check.Name}}": { "status": "{{check.Status}}", "description": null, "data": {} }
                    """));

        return $$"""{ "status": "{{status}}", "results": { {{results}} } }""";
    }

    /// <summary>
    /// The key is generated rather than written down: any literal assigned to a key reads as a
    /// leaked credential to secret scanning, and push protection then refuses the commit.
    /// </summary>
    private static KrdsOptions DataBridge(string? baseUrl = BaseUrl, bool withKey = true) => new()
    {
        Provider = nameof(KrdsSnapshotProvider.DataBridge),
        BaseUrl = baseUrl,
        ApiKey = withKey ? Guid.NewGuid().ToString("N") : null
    };

    private static (KrdsDiagnostics Diagnostics, StubHandler Handler) Probe(
        KrdsOptions options,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? timeout = null)
    {
        var handler = new StubHandler(respond);
        return (new KrdsDiagnostics(options, new StubFactory(handler), timeout), handler);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(
        HttpStatusCode status, string body) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    [Theory]
    [InlineData(BaseUrl)]
    [InlineData(BaseUrl + "/")]
    [InlineData("  " + BaseUrl + "  ")]
    public void Only_the_host_name_of_the_bridge_is_exposed(string baseUrl)
    {
        Assert.Equal(Host, new KrdsDiagnostics(DataBridge(baseUrl), null).Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/etc/passwd")]
    [InlineData("ftp://example.com")]
    public async Task Nothing_is_called_without_a_usable_bridge_url(string? baseUrl)
    {
        var (diagnostics, handler) = Probe(DataBridge(baseUrl), Answer(HttpStatusCode.OK, HealthReport("Healthy")));

        var health = await diagnostics.ProbeAsync();
        var access = await diagnostics.ProbeAccessAsync();

        Assert.All([health, access], result =>
        {
            Assert.Equal(ProbeState.Unknown, result.State);
            Assert.Contains("Rules:Krds:BaseUrl", result.Detail, StringComparison.Ordinal);
        });
        Assert.Null(diagnostics.Target);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_healthy_bridge_is_asked_at_its_health_endpoint_without_the_key()
    {
        var (diagnostics, handler) = Probe(DataBridge(), Answer(HttpStatusCode.OK, HealthReport("Healthy")));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Ok, result.State);
        Assert.Equal("Healthy, 200 from /health", result.Detail);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri($"{BaseUrl}/health"), request.RequestUri);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task A_degraded_bridge_still_works_and_names_what_is_degraded()
    {
        var (diagnostics, _) = Probe(
            DataBridge(),
            Answer(HttpStatusCode.OK, HealthReport("Degraded", ("mongodb", "Healthy"), ("quartz_jobs", "Degraded"))));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Ok, result.State);
        Assert.Equal("Degraded, 200 from /health. Not healthy: quartz_jobs (Degraded)", result.Detail);
    }

    [Fact]
    public async Task An_unhealthy_bridge_fails_and_names_what_is_unhealthy()
    {
        var (diagnostics, _) = Probe(
            DataBridge(),
            Answer(
                HttpStatusCode.ServiceUnavailable,
                HealthReport("Unhealthy", ("mongodb", "Unhealthy"), ("aws_s3", "Healthy"), ("aws_sns", "Degraded"))));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal(
            "Unhealthy, 503 from /health. Not healthy: mongodb (Unhealthy), aws_sns (Degraded)",
            result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "Alive!", ProbeState.Ok, "200 from /health")]
    [InlineData(HttpStatusCode.NotFound, "", ProbeState.Failed, "404 Not Found from /health")]
    [InlineData(HttpStatusCode.BadGateway, "<html>proxy</html>", ProbeState.Failed, "502 Bad Gateway from /health")]
    public async Task An_answer_that_is_not_a_health_report_is_judged_on_its_status(
        HttpStatusCode status, string body, ProbeState expected, string detail)
    {
        var (diagnostics, _) = Probe(DataBridge(), Answer(status, body));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(expected, result.State);
        Assert.Equal(detail, result.Detail);
    }

    [Fact]
    public async Task An_unreachable_bridge_fails_with_the_reason()
    {
        var (diagnostics, _) = Probe(
            DataBridge(),
            (_, _) => throw new HttpRequestException($"Name or service not known ({Host}:443)"));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Contains("Name or service not known", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bridge_that_does_not_answer_in_time_fails_rather_than_hanging()
    {
        var (diagnostics, _) = Probe(
            DataBridge(),
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            timeout: TimeSpan.FromMilliseconds(100));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal("No answer from /health within 0.1 seconds.", result.Detail);
    }

    [Fact]
    public async Task Cancelling_the_request_cancels_the_probe()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (diagnostics, _) = Probe(
            DataBridge(),
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostics.ProbeAsync(cancelled.Token));
    }

    [Fact]
    public async Task A_missing_key_does_not_stop_the_health_check()
    {
        var (diagnostics, handler) = Probe(DataBridge(withKey: false), Answer(HttpStatusCode.OK, HealthReport("Healthy")));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Ok, result.State);
        Assert.Equal("Healthy, 200 from /health", result.Detail);
        Assert.Single(handler.Requests);
    }

    /// <summary>What the bridge answers for the latest snapshot, as captured from dev.</summary>
    private static string LatestSnapshot(string downloadUrl) => $$"""
        {
          "objectKey": "views/krds-db_20261019070003.sqlite",
          "downloadUrl": "{{downloadUrl}}",
          "size": 851443712,
          "lastModified": "2026-10-19T07:00:03+00:00",
          "expiresAt": "2026-10-23T14:41:00Z"
        }
        """;

    /// <summary>Generated, like the key, so nothing here reads as a real presigned URL.</summary>
    private static string DownloadUrl() =>
        $"https://snapshots.example/views/krds-db_20261019070003.sqlite?signature={Guid.NewGuid():N}";

    [Fact]
    public async Task Access_is_checked_by_reading_the_latest_snapshot_with_the_key()
    {
        var options = DataBridge();
        var (diagnostics, handler) = Probe(options, Answer(HttpStatusCode.OK, LatestSnapshot(DownloadUrl())));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Ok, result.State);
        Assert.Equal(
            "API key accepted, 200 from /api/etl/staging/sqlite/latest. Latest snapshot " +
            "views/krds-db_20261019070003.sqlite, 812 MB, last modified 2026-10-19 07:00 UTC",
            result.Detail);

        // The same read a rule run makes, asking for the shortest-lived URL the bridge mints.
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri($"{BaseUrl}/api/etl/staging/sqlite/latest?expiresInMinutes=1"), request.RequestUri);
        Assert.Equal("ApiKey", request.Headers.Authorization?.Scheme);
        Assert.Equal(options.ApiKey, request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Neither_the_presigned_url_nor_the_key_is_ever_repeated()
    {
        var options = DataBridge();
        var downloadUrl = DownloadUrl();
        var (diagnostics, _) = Probe(options, Answer(HttpStatusCode.OK, LatestSnapshot(downloadUrl)));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.DoesNotContain(downloadUrl, result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(options.ApiKey!, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_key_fails_and_names_the_setting_to_fix()
    {
        var options = DataBridge();
        var (diagnostics, _) = Probe(options, Answer(HttpStatusCode.Unauthorized, ""));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal(
            "The Data Bridge refused the API key, 401 Unauthorized from /api/etl/staging/sqlite/latest. " +
            "Rules:Krds:ApiKey must be the key the KRDS team issued for this environment.",
            result.Detail);
        Assert.DoesNotContain(options.ApiKey!, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bridge_with_no_snapshot_to_give_fails_with_its_own_explanation()
    {
        var (diagnostics, _) = Probe(
            DataBridge(),
            Answer(
                HttpStatusCode.NotFound,
                """{ "message": "No SQLite read model found. Run the ETL pipeline first.", "timestamp": "2026-10-23T14:40:00Z" }"""));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal(
            "404 Not Found from /api/etl/staging/sqlite/latest: No SQLite read model found. Run the ETL pipeline first.",
            result.Detail);
    }

    [Theory]
    [InlineData("<html>Sign in to continue</html>")]
    [InlineData("""{ "objectKey": "views/krds-db.sqlite" }""")]
    [InlineData("")]
    public async Task A_success_that_offers_nothing_to_download_is_not_access(string body)
    {
        var (diagnostics, _) = Probe(DataBridge(), Answer(HttpStatusCode.OK, body));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal(
            "The API key was accepted, but the 200 from /api/etl/staging/sqlite/latest offered no snapshot to download.",
            result.Detail);
    }

    [Fact]
    public async Task A_read_that_does_not_answer_in_time_fails_rather_than_hanging()
    {
        var (diagnostics, _) = Probe(
            DataBridge(),
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            timeout: TimeSpan.FromMilliseconds(100));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal("No answer from /api/etl/staging/sqlite/latest within 0.1 seconds.", result.Detail);
    }

    [Fact]
    public async Task An_unreachable_bridge_cannot_be_read_either()
    {
        var (diagnostics, _) = Probe(
            DataBridge(),
            (_, _) => throw new HttpRequestException($"Name or service not known ({Host}:443)"));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Contains("Name or service not known", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_without_a_key_fails_without_asking()
    {
        var (diagnostics, handler) = Probe(DataBridge(withKey: false), Answer(HttpStatusCode.OK, LatestSnapshot(DownloadUrl())));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal(
            "Rules:Krds:ApiKey is not set, so the Data Bridge cannot be read and no snapshot can be downloaded.",
            result.Detail);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancelling_the_request_cancels_the_access_probe()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (diagnostics, _) = Probe(
            DataBridge(),
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostics.ProbeAccessAsync(cancelled.Token));
    }

    [Fact]
    public async Task A_local_snapshot_is_checked_on_disk_and_never_over_http()
    {
        var path = Path.GetTempFileName();
        try
        {
            var options = new KrdsOptions { Provider = nameof(KrdsSnapshotProvider.LocalFile), LocalFilePath = path };
            var (diagnostics, handler) = Probe(options, Answer(HttpStatusCode.OK, HealthReport("Healthy")));

            var result = await diagnostics.ProbeAsync();

            Assert.Equal(ProbeState.Ok, result.State);
            Assert.Equal(path, diagnostics.Target);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static KrdsOptions LocalFile(string path) =>
        new() { Provider = nameof(KrdsSnapshotProvider.LocalFile), LocalFilePath = path };

    [Fact]
    public async Task A_local_snapshot_is_read_the_way_a_run_reads_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"krds-probe-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE CtsOpenLocation (Cph TEXT); CREATE TABLE Holding (Cph TEXT);";
                await command.ExecuteNonQueryAsync();
            }

            var (diagnostics, handler) = Probe(LocalFile(path), Answer(HttpStatusCode.OK, ""));

            var result = await diagnostics.ProbeAccessAsync();

            Assert.Equal(ProbeState.Ok, result.State);
            Assert.Equal("Opened read-only: 2 tables", result.Detail);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("", "opened, but holds no tables")]
    [InlineData("This is a text file, not a SQLite database.", "file is not a database")]
    public async Task A_local_file_that_cannot_be_read_as_a_snapshot_fails(string content, string reason)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);

            var (diagnostics, _) = Probe(LocalFile(path), Answer(HttpStatusCode.OK, ""));

            var result = await diagnostics.ProbeAccessAsync();

            Assert.Equal(ProbeState.Failed, result.State);
            Assert.Contains(reason, result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_missing_local_snapshot_cannot_be_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"krds-probe-{Guid.NewGuid():N}.sqlite");
        var (diagnostics, _) = Probe(LocalFile(path), Answer(HttpStatusCode.OK, ""));

        var result = await diagnostics.ProbeAccessAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal($"The snapshot '{path}' does not exist.", result.Detail);
        Assert.False(File.Exists(path));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
