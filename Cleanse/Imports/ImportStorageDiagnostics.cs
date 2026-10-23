using System.Diagnostics;
using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Cleanse.Core.Abstractions;

namespace Cleanse.Api.Imports;

/// <summary>
/// Proves the bucket can be reached with the service's own credentials. HeadBucket rather than a
/// write, so checking leaves nothing behind; CDP grants a service list access to its own bucket,
/// which is all HeadBucket needs.
/// </summary>
internal sealed class S3ImportStorageDiagnostics(IAmazonS3 client, string bucketName, TimeSpan? timeout = null)
    : IImportStorageDiagnostics
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public string ProviderName => "S3";

    public string? Target => bucketName;

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        // Bounded separately from the request, so a hung connection reports itself here instead
        // of running the whole status page into its request timeout.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            var response = await client.HeadBucketAsync(
                new HeadBucketRequest { BucketName = bucketName }, deadline.Token);

            return ProbeResult.Ok(
                Elapsed(started),
                string.IsNullOrWhiteSpace(response.BucketRegion)
                    ? "Bucket reachable"
                    : $"Bucket reachable in {response.BucketRegion}");
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
                    $"S3 did not answer within {_timeout.TotalSeconds:0.#} seconds."));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return ProbeResult.Failed(Elapsed(started), $"The bucket '{bucketName}' does not exist.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return ProbeResult.Failed(
                Elapsed(started), $"Access to the bucket '{bucketName}' was denied.");
        }
        catch (Exception ex)
        {
            return ProbeResult.Failed(Elapsed(started), ex.Message);
        }
    }

    private static double Elapsed(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;
}

/// <summary>
/// Development's directory store. A directory that does not exist yet is not a fault: the first
/// upload creates it.
/// </summary>
internal sealed class FileSystemImportStorageDiagnostics(string rootPath) : IImportStorageDiagnostics
{
    public string ProviderName => "FileSystem";

    public string? Target => rootPath;

    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var detail = Directory.Exists(rootPath)
            ? "Directory exists"
            : "Directory not created yet; the first upload creates it";

        return Task.FromResult(ProbeResult.Ok(Stopwatch.GetElapsedTime(started).TotalMilliseconds, detail));
    }
}

/// <summary>
/// Reported as failed rather than unknown: the upload endpoint is always live, so without a bucket
/// every upload is refused.
/// </summary>
internal sealed class UnconfiguredImportStorageDiagnostics : IImportStorageDiagnostics
{
    public string ProviderName => "S3";

    public string? Target => null;

    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProbeResult.Failed(
            0,
            "Imports:S3BucketName is not set for this environment, so uploads are refused."));
}
