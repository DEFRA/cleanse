using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cleanse.Core.Abstractions;

namespace Cleanse.Rules.Krds;

/// <summary>
/// Reports whether the snapshot source could be reached, without acquiring a snapshot: the real
/// thing is the better part of a gigabyte, so a status page must not pull one.
/// </summary>
/// <remarks>
/// The Data Bridge is asked through its own anonymous <c>/health</c> endpoint. That needs no key
/// and mints no presigned URL, and the answer carries the bridge's view of its own dependencies,
/// which is usually the more useful thing to know when snapshots stop arriving.
/// </remarks>
internal sealed class KrdsDiagnostics(
    KrdsOptions options,
    IHttpClientFactory? httpClientFactory,
    TimeSpan? timeout = null) : IKrdsDiagnostics
{
    internal const string HealthClientName = "krds-data-bridge-health";
    internal const string HealthPath = "health";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A health report runs to a few kilobytes; anything far larger is not one.</summary>
    private const int MaxBodyBytes = 64 * 1024;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public string ProviderName => options.ResolveProvider()?.ToString() ?? options.Provider;

    /// <summary>The snapshot path, or for the Data Bridge only the host name of its URL.</summary>
    public string? Target => options.ResolveProvider() switch
    {
        KrdsSnapshotProvider.LocalFile => options.LocalFilePath,
        KrdsSnapshotProvider.DataBridge => TryGetBaseUri(out var baseUri) ? baseUri.Host : null,
        _ => null
    };

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        switch (options.ResolveProvider())
        {
            case KrdsSnapshotProvider.LocalFile:
                return options.Describe() is { } problem ? ProbeResult.Unknown(problem) : ProbeLocalFile();

            // The health check is anonymous, so a usable URL is all it takes to ask. A missing key
            // is still reported, because without one no snapshot can be downloaded.
            case KrdsSnapshotProvider.DataBridge when TryGetBaseUri(out var baseUri):
            {
                var result = await PingDataBridgeAsync(baseUri, cancellationToken);
                return string.IsNullOrWhiteSpace(options.ApiKey)
                    ? result with
                    {
                        Detail = Append(
                            result.Detail,
                            "Rules:Krds:ApiKey is not set, so snapshots cannot be downloaded.")
                    }
                    : result;
            }

            default:
                return ProbeResult.Unknown(
                    options.Describe() ?? $"Rules:Krds:BaseUrl is not an http(s) URL: '{options.BaseUrl}'.");
        }
    }

    private ProbeResult ProbeLocalFile()
    {
        var started = Stopwatch.GetTimestamp();
        var path = options.LocalFilePath!;

        try
        {
            if (!File.Exists(path))
            {
                return ProbeResult.Failed(Elapsed(started), $"The snapshot '{path}' does not exist.");
            }

            var size = new FileInfo(path).Length;
            return ProbeResult.Ok(Elapsed(started), $"{size / 1024 / 1024} MB on disk");
        }
        catch (Exception ex)
        {
            return ProbeResult.Failed(Elapsed(started), ex.Message);
        }
    }

    private async Task<ProbeResult> PingDataBridgeAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        if (httpClientFactory is null)
        {
            return ProbeResult.Unknown("No HTTP client factory is registered in this host.");
        }

        var started = Stopwatch.GetTimestamp();

        // Bounded separately from the request, so a hung connection reports itself here instead
        // of running the whole status page into its request timeout.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            var client = httpClientFactory.CreateClient(HealthClientName);
            using var response = await client.GetAsync(
                new Uri(baseUri, HealthPath), HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            var body = await ReadBoundedAsync(response.Content, deadline.Token);
            return Interpret((int)response.StatusCode, response.ReasonPhrase, body, Elapsed(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Failed(
                Elapsed(started),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No answer from /{HealthPath} within {_timeout.TotalSeconds:0.#} seconds."));
        }
        catch (Exception ex)
        {
            return ProbeResult.Failed(Elapsed(started), ex.Message);
        }
    }

    /// <summary>
    /// Healthy and Degraded both answer 200 and keep serving, so both count as working, with any
    /// check that is not healthy named. Unhealthy, or any error status, does not.
    /// </summary>
    internal static ProbeResult Interpret(int statusCode, string? reasonPhrase, string body, double elapsedMs)
    {
        var success = statusCode is >= 200 and < 300;
        var source = $"{statusCode} from /{HealthPath}";

        if (TryReadHealthReport(body) is not { } report)
        {
            return success
                ? ProbeResult.Ok(elapsedMs, source)
                : ProbeResult.Failed(
                    elapsedMs,
                    string.IsNullOrWhiteSpace(reasonPhrase)
                        ? source
                        : $"{statusCode} {reasonPhrase} from /{HealthPath}");
        }

        var summary = report.NotHealthy.Count == 0
            ? $"{report.Status}, {source}"
            : $"{report.Status}, {source}. Not healthy: {string.Join(", ", report.NotHealthy)}";

        return success && !report.Status.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase)
            ? ProbeResult.Ok(elapsedMs, summary)
            : ProbeResult.Failed(elapsedMs, summary);
    }

    private sealed record HealthReport(string Status, IReadOnlyList<string> NotHealthy);

    /// <summary>
    /// Reads the ASP.NET Core health report the Data Bridge writes: an overall <c>status</c> and
    /// a <c>results</c> object holding each check's own <c>status</c>.
    /// </summary>
    private static HealthReport? TryReadHealthReport(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var notHealthy = new List<string>();
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object)
            {
                foreach (var check in results.EnumerateObject())
                {
                    var checkStatus = check.Value.ValueKind == JsonValueKind.Object &&
                                      check.Value.TryGetProperty("status", out var value) &&
                                      value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;

                    if (!string.Equals(checkStatus, "Healthy", StringComparison.OrdinalIgnoreCase))
                    {
                        notHealthy.Add($"{check.Name} ({checkStatus ?? "no status"})");
                    }
                }
            }

            return new HealthReport(status.GetString()!, notHealthy);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes];
        var read = await stream.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>
    /// Only http and https: on Linux <see cref="Uri"/> also reads a bare path as an absolute
    /// file URI, which would otherwise pass for a configured bridge.
    /// </summary>
    private bool TryGetBaseUri([NotNullWhen(true)] out Uri? baseUri)
    {
        baseUri = null;

        if (string.IsNullOrWhiteSpace(options.BaseUrl) ||
            !Uri.TryCreate(options.BaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        baseUri = uri;
        return true;
    }

    private static string Append(string? detail, string note) =>
        string.IsNullOrWhiteSpace(detail) ? note : $"{detail.TrimEnd('.')}. {note}";

    private static double Elapsed(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;
}
