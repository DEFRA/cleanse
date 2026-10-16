using System.Diagnostics;
using System.Net.Http.Headers;
using Cleanse.Core.Abstractions;
using Cleanse.Rules.Krds.Snapshots;

namespace Cleanse.Rules.Krds;

/// <summary>
/// Reports whether the snapshot source could be reached, without acquiring a snapshot: the real
/// thing is the better part of a gigabyte, so a status page must not pull one.
/// </summary>
internal sealed class KrdsDiagnostics(KrdsOptions options, IHttpClientFactory? httpClientFactory)
    : IKrdsDiagnostics
{
    public string ProviderName => options.ResolveProvider()?.ToString() ?? options.Provider;

    public string? Target => options.ResolveProvider() switch
    {
        KrdsSnapshotProvider.LocalFile => options.LocalFilePath,
        KrdsSnapshotProvider.DataBridge => options.BaseUrl,
        _ => null
    };

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (options.Describe() is { } problem)
        {
            return ProbeResult.Unknown(problem);
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            return options.ResolveProvider() switch
            {
                KrdsSnapshotProvider.LocalFile => ProbeLocalFile(started),
                KrdsSnapshotProvider.DataBridge => await ProbeDataBridgeAsync(started, cancellationToken),
                _ => ProbeResult.Unknown($"'{options.Provider}' is not a known KRDS provider.")
            };
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

    private ProbeResult ProbeLocalFile(long started)
    {
        var path = options.LocalFilePath!;

        if (!File.Exists(path))
        {
            return ProbeResult.Failed(Elapsed(started), $"The snapshot '{path}' does not exist.");
        }

        var size = new FileInfo(path).Length;
        return ProbeResult.Ok(Elapsed(started), $"{size / 1024 / 1024} MB on disk");
    }

    /// <summary>
    /// Asks for the metadata of the latest snapshot, which proves reachability, the key and the
    /// route through the proxy without transferring the file itself.
    /// </summary>
    private async Task<ProbeResult> ProbeDataBridgeAsync(long started, CancellationToken cancellationToken)
    {
        if (httpClientFactory is null)
        {
            return ProbeResult.Unknown("No HTTP client factory is registered in this host.");
        }

        var client = httpClientFactory.CreateClient(DataBridgeSnapshotSource.ApiClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, DataBridgeSnapshotSource.LatestPath);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", options.ApiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken);

        return response.IsSuccessStatusCode
            ? ProbeResult.Ok(Elapsed(started), $"{(int)response.StatusCode} from the Data Bridge")
            : ProbeResult.Failed(
                Elapsed(started),
                $"The Data Bridge answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static double Elapsed(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;
}
