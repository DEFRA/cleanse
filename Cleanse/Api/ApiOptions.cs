namespace Cleanse.Api;

public sealed class ApiOptions
{
    /// <summary>
    /// The ceiling on one bulk detection request. API Gateway's 29 second integration timeout
    /// cannot be raised, so the batch has to be small enough to finish inside it even against a
    /// cold Aurora cluster. The rules engine chunks to suit.
    /// </summary>
    public int MaxDetectionsPerRequest { get; set; } = 1000;

    /// <summary>Sits inside the gateway's limit so we return a clean 504 rather than being cut off.</summary>
    public int RequestTimeoutSeconds { get; set; } = 25;

    /// <summary>Bounds the body independently of the item count, since contexts vary in size.</summary>
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDetectionsPerRequest, 1, nameof(MaxDetectionsPerRequest));
        ArgumentOutOfRangeException.ThrowIfLessThan(RequestTimeoutSeconds, 1, nameof(RequestTimeoutSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRequestBodyBytes, 1024, nameof(MaxRequestBodyBytes));
    }
}
