using System.Net;
using System.Text;
using Cleanse.Core.Abstractions;
using Cleanse.Rules.Krds;

namespace Cleanse.Rules.Test;

/// <summary>
/// The KRDS row on the status page. The Data Bridge is checked through its own anonymous /health
/// endpoint, and only its host name is ever shown.
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

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Unknown, result.State);
        Assert.Contains("Rules:Krds:BaseUrl", result.Detail, StringComparison.Ordinal);
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
    public async Task A_missing_key_does_not_stop_the_check_but_is_reported()
    {
        var (diagnostics, handler) = Probe(DataBridge(withKey: false), Answer(HttpStatusCode.OK, HealthReport("Healthy")));

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Ok, result.State);
        Assert.Equal(
            "Healthy, 200 from /health. Rules:Krds:ApiKey is not set, so snapshots cannot be downloaded.",
            result.Detail);
        Assert.Single(handler.Requests);
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
