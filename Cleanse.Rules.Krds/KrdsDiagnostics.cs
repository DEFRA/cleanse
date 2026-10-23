using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cleanse.Core.Abstractions;
using Cleanse.Rules.Krds.Snapshots;
using Microsoft.Data.Sqlite;

namespace Cleanse.Rules.Krds;

/// <summary>
/// Reports whether the snapshot source could be reached, and whether it could be read, without
/// acquiring a snapshot: the real thing is the better part of a gigabyte, so a status page must
/// not pull one.
/// </summary>
/// <remarks>
/// Two probes, because a bridge that answers is not one that lets this service in.
/// <see cref="ProbeAsync"/> asks the Data Bridge's own anonymous <c>/health</c> endpoint, whose
/// answer carries the bridge's view of its own dependencies. <see cref="ProbeAccessAsync"/> asks
/// for the latest snapshot as a run does, key and all, so it fails where the health check cannot:
/// on a wrong or missing key, or on a bridge with no snapshot to give.
/// </remarks>
internal sealed class KrdsDiagnostics(
    KrdsOptions options,
    IHttpClientFactory? httpClientFactory,
    TimeSpan? timeout = null) : IKrdsDiagnostics
{
    internal const string ClientName = "krds-data-bridge-diagnostics";
    internal const string HealthPath = "health";

    /// <summary>
    /// The read a run makes, asking for the shortest-lived presigned URL the bridge will mint,
    /// because the probe never uses it.
    /// </summary>
    internal const string AccessPath = DataBridgeSnapshotSource.LatestPath + "?expiresInMinutes=1";

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A health report or a snapshot description runs to a few kilobytes; anything far larger is neither.</summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>As <c>ReadFromJsonAsync</c> reads the same answer when a run asks for it.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

            // The health check is anonymous, so a usable URL is all it takes to ask. Whether the
            // key works is the access probe's question.
            case KrdsSnapshotProvider.DataBridge when TryGetBaseUri(out var baseUri):
                return await AskDataBridgeAsync(baseUri, HealthPath, withKey: false, InterpretHealth, cancellationToken);

            default:
                return ProbeResult.Unknown(UnusableConfiguration());
        }
    }

    public async Task<ProbeResult> ProbeAccessAsync(CancellationToken cancellationToken = default)
    {
        switch (options.ResolveProvider())
        {
            case KrdsSnapshotProvider.LocalFile:
                return options.Describe() is { } problem
                    ? ProbeResult.Unknown(problem)
                    : await ReadLocalFileAsync(cancellationToken);

            // Asking without a key would only prove that the bridge turns strangers away.
            case KrdsSnapshotProvider.DataBridge when TryGetBaseUri(out _) && string.IsNullOrWhiteSpace(options.ApiKey):
                return ProbeResult.Failed(
                    0,
                    "Rules:Krds:ApiKey is not set, so the Data Bridge cannot be read and no snapshot can be downloaded.");

            case KrdsSnapshotProvider.DataBridge when TryGetBaseUri(out var baseUri):
                return await AskDataBridgeAsync(baseUri, AccessPath, withKey: true, InterpretAccess, cancellationToken);

            default:
                return ProbeResult.Unknown(UnusableConfiguration());
        }
    }

    private string UnusableConfiguration() =>
        options.Describe() ?? $"Rules:Krds:BaseUrl is not an http(s) URL: '{options.BaseUrl}'.";

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

    /// <summary>
    /// Opens the snapshot as a run does, read-only, and reads its schema: a file that exists can
    /// still be something SQLite cannot read, or an empty database with nothing for the rules.
    /// </summary>
    private async Task<ProbeResult> ReadLocalFileAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var path = options.LocalFilePath!;

        if (!File.Exists(path))
        {
            return ProbeResult.Failed(Elapsed(started), $"The snapshot '{path}' does not exist.");
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                // Closed with the probe rather than kept open by the pool, holding the file.
                Pooling = false
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";
            var tables = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            return tables == 0
                ? ProbeResult.Failed(Elapsed(started), $"The snapshot '{path}' opened, but holds no tables.")
                : ProbeResult.Ok(
                    Elapsed(started),
                    string.Create(CultureInfo.InvariantCulture, $"Opened read-only: {tables} {(tables == 1 ? "table" : "tables")}"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProbeResult.Failed(Elapsed(started), ex.Message);
        }
    }

    /// <summary>
    /// One GET to the bridge, with the key only when asked for: the health check is anonymous, and
    /// a key is never sent where it is not needed.
    /// </summary>
    private async Task<ProbeResult> AskDataBridgeAsync(
        Uri baseUri,
        string path,
        bool withKey,
        Func<int, string?, string, double, ProbeResult> interpret,
        CancellationToken cancellationToken)
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
            var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            if (withKey)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    DataBridgeSnapshotSource.AuthorizationScheme, options.ApiKey);
            }

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            var body = await ReadBoundedAsync(response.Content, deadline.Token);
            return interpret((int)response.StatusCode, response.ReasonPhrase, body, Elapsed(started));
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
                    $"No answer from /{WithoutQuery(path)} within {_timeout.TotalSeconds:0.#} seconds."));
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
    internal static ProbeResult InterpretHealth(int statusCode, string? reasonPhrase, string body, double elapsedMs)
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

    /// <summary>
    /// Access means what a run needs: the key accepted and a snapshot offered for download. The
    /// download URL is presigned, so it is only checked for, and never repeated.
    /// </summary>
    internal static ProbeResult InterpretAccess(int statusCode, string? reasonPhrase, string body, double elapsedMs)
    {
        var path = $"/{DataBridgeSnapshotSource.LatestPath}";

        if (statusCode is >= 200 and < 300)
        {
            var latest = TryRead<StagingDatabaseLatestResponse>(body);

            return string.IsNullOrWhiteSpace(latest?.DownloadUrl)
                ? ProbeResult.Failed(
                    elapsedMs,
                    $"The API key was accepted, but the {statusCode} from {path} offered no snapshot to download.")
                : ProbeResult.Ok(
                    elapsedMs,
                    $"API key accepted, {statusCode} from {path}. Latest snapshot {DescribeSnapshot(latest)}");
        }

        var answer = string.IsNullOrWhiteSpace(reasonPhrase)
            ? $"{statusCode} from {path}"
            : $"{statusCode} {reasonPhrase} from {path}";

        if (statusCode == 401)
        {
            return ProbeResult.Failed(
                elapsedMs,
                $"The Data Bridge refused the API key, {answer}. Rules:Krds:ApiKey must be the key " +
                "the KRDS team issued for this environment.");
        }

        // The bridge explains a refusal in a JSON message, for example that no snapshot exists yet.
        return ProbeResult.Failed(
            elapsedMs,
            TryRead<BridgeError>(body)?.Message is { Length: > 0 } message
                ? $"{answer}: {Truncate(message.Trim())}"
                : answer);
    }

    private static string DescribeSnapshot(StagingDatabaseLatestResponse latest)
    {
        var parts = new List<string> { string.IsNullOrWhiteSpace(latest.ObjectKey) ? "(unnamed)" : latest.ObjectKey };

        if (latest.Size is { } size)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{size / 1024 / 1024} MB"));
        }

        if (latest.LastModified is { } modified)
        {
            parts.Add("last modified " +
                      modified.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        }

        return string.Join(", ", parts);
    }

    /// <summary>What the Data Bridge sends with a refusal.</summary>
    private sealed record BridgeError(string? Message);

    private static T? TryRead<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static string WithoutQuery(string path) =>
        path.IndexOf('?', StringComparison.Ordinal) is var query and >= 0 ? path[..query] : path;

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    private static double Elapsed(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;
}
