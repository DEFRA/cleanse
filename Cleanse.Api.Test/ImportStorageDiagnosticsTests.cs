using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Cleanse.Api.Imports;
using Cleanse.Core.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cleanse.Api.Test;

/// <summary>
/// The storage row on the status page: whether uploaded import files have somewhere to go.
/// </summary>
public class ImportStorageDiagnosticsTests
{
    private const string Bucket = "ext-test-cleanse-8ec5c";

    private static IAmazonS3 S3Answering(HeadBucketResponse response)
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>()).Returns(response);
        return s3;
    }

    private static IAmazonS3 S3Throwing(Exception exception)
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(exception);
        return s3;
    }

    [Fact]
    public async Task A_reachable_bucket_is_reported_by_name_and_region()
    {
        var s3 = S3Answering(new HeadBucketResponse { BucketRegion = "eu-west-2" });
        var diagnostics = new S3ImportStorageDiagnostics(s3, Bucket);

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Ok, result.State);
        Assert.Equal("Bucket reachable in eu-west-2", result.Detail);
        Assert.Equal("S3", diagnostics.ProviderName);
        Assert.Equal(Bucket, diagnostics.Target);
        await s3.Received(1).HeadBucketAsync(
            Arg.Is<HeadBucketRequest>(request => request.BucketName == Bucket),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reachable_bucket_without_a_region_still_reads_cleanly()
    {
        var result = await new S3ImportStorageDiagnostics(S3Answering(new HeadBucketResponse()), Bucket).ProbeAsync();

        Assert.Equal(ProbeState.Ok, result.State);
        Assert.Equal("Bucket reachable", result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, $"The bucket '{Bucket}' does not exist.")]
    [InlineData(HttpStatusCode.Forbidden, $"Access to the bucket '{Bucket}' was denied.")]
    public async Task A_bucket_that_cannot_be_used_fails_with_the_reason(HttpStatusCode status, string detail)
    {
        var s3 = S3Throwing(new AmazonS3Exception("S3 says no") { StatusCode = status });

        var result = await new S3ImportStorageDiagnostics(s3, Bucket).ProbeAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal(detail, result.Detail);
    }

    [Fact]
    public async Task Any_other_failure_is_reported_rather_than_thrown()
    {
        var s3 = S3Throwing(new HttpRequestException("Name or service not known (s3.eu-west-2.amazonaws.com:443)"));

        var result = await new S3ImportStorageDiagnostics(s3, Bucket).ProbeAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Contains("Name or service not known", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3_that_does_not_answer_in_time_fails_rather_than_hanging()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => NeverAnswer(call.Arg<CancellationToken>()));

        var result = await new S3ImportStorageDiagnostics(s3, Bucket, TimeSpan.FromMilliseconds(100)).ProbeAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Equal("S3 did not answer within 0.1 seconds.", result.Detail);
    }

    [Fact]
    public async Task Cancelling_the_request_cancels_the_probe()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var s3 = Substitute.For<IAmazonS3>();
        s3.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => NeverAnswer(call.Arg<CancellationToken>()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new S3ImportStorageDiagnostics(s3, Bucket).ProbeAsync(cancelled.Token));
    }

    [Fact]
    public async Task No_bucket_is_a_failure_that_says_which_setting_is_missing()
    {
        var diagnostics = new UnconfiguredImportStorageDiagnostics();

        var result = await diagnostics.ProbeAsync();

        Assert.Equal(ProbeState.Failed, result.State);
        Assert.Contains("Imports:S3BucketName", result.Detail, StringComparison.Ordinal);
        Assert.Equal("S3", diagnostics.ProviderName);
        Assert.Null(diagnostics.Target);
    }

    [Fact]
    public async Task A_local_directory_is_fine_whether_or_not_it_exists_yet()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cleanse-imports-{Guid.NewGuid():N}");
        var diagnostics = new FileSystemImportStorageDiagnostics(root);

        var before = await diagnostics.ProbeAsync();
        Directory.CreateDirectory(root);
        try
        {
            var after = await diagnostics.ProbeAsync();

            Assert.Equal(ProbeState.Ok, before.State);
            Assert.Contains("first upload", before.Detail, StringComparison.Ordinal);
            Assert.Equal(ProbeState.Ok, after.State);
            Assert.Equal("Directory exists", after.Detail);
            Assert.Equal(root, diagnostics.Target);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    private static async Task<HeadBucketResponse> NeverAnswer(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new HeadBucketResponse();
    }
}
