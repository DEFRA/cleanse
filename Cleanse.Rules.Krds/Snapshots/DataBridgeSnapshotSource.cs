using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Krds.Snapshots;

/// <summary>What <c>GET /api/etl/staging/sqlite/latest</c> returns.</summary>
internal sealed record StagingDatabaseLatestResponse
{
    [JsonPropertyName("objectKey")]
    public string? ObjectKey { get; init; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Downloads the latest SQLite snapshot from the KRDS Data Bridge.
/// </summary>
/// <remarks>
/// Two hops. The first asks the Data Bridge for a presigned URL and carries the Data Bridge key;
/// the second fetches the object from object storage and must not, because a presigned URL is
/// already authenticated and an extra Authorization header makes some stores reject it.
/// </remarks>
internal sealed class DataBridgeSnapshotSource(
    IHttpClientFactory httpClientFactory,
    KrdsOptions options,
    ILogger<DataBridgeSnapshotSource> logger) : IKrdsSnapshotSource
{
    internal const string ApiClientName = "krds-data-bridge";
    internal const string DownloadClientName = "krds-snapshot-download";
    internal const string LatestPath = "api/etl/staging/sqlite/latest";

    public string Description => $"KRDS Data Bridge {options.BaseUrl}";

    public async Task<KrdsSnapshot> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(latest.DownloadUrl))
        {
            throw new InvalidOperationException(
                "The KRDS Data Bridge returned no download URL for the latest SQLite snapshot.");
        }

        logger.LogInformation(
            "Downloading KRDS snapshot {ObjectKey} ({Size} bytes, last modified {LastModified})",
            latest.ObjectKey, latest.Size, latest.LastModified);

        var path = CreateScratchPath();
        try
        {
            await DownloadAsync(latest.DownloadUrl, path, cancellationToken);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return new KrdsSnapshot(path, ownsFile: true, $"{Description} object {latest.ObjectKey}");
    }

    private async Task<StagingDatabaseLatestResponse> GetLatestAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ApiClientName);

        var expiry = Math.Clamp(options.DownloadUrlExpiryMinutes, 1, 60);
        var url = $"{LatestPath}?expiresInMinutes={expiry.ToString(CultureInfo.InvariantCulture)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", options.ApiKey);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"The KRDS Data Bridge returned {(int)response.StatusCode} for {LatestPath}: {Truncate(body)}");
        }

        return await response.Content.ReadFromJsonAsync<StagingDatabaseLatestResponse>(cancellationToken)
               ?? throw new InvalidOperationException(
                   "The KRDS Data Bridge returned an empty response for the latest SQLite snapshot.");
    }

    private async Task DownloadAsync(string downloadUrl, string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(DownloadClientName);

        using var response = await client.GetAsync(
            downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Downloading the KRDS snapshot returned {(int)response.StatusCode}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024, useAsync: true);

        await source.CopyToAsync(destination, cancellationToken);
    }

    private string CreateScratchPath()
    {
        var directory = string.IsNullOrWhiteSpace(options.ScratchDirectory)
            ? Path.GetTempPath()
            : options.ScratchDirectory;

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"krds-snapshot-{Guid.NewGuid():n}.sqlite");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512] + "…";
}
