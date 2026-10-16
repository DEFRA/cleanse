namespace Cleanse.Core;

public sealed class CleanseCoreOptions
{
    /// <summary>How many detections are read, merged and written per round trip.</summary>
    public int DetectionBatchSize { get; set; } = 500;

    public int SuppressionExpiryBatchSize { get; set; } = 500;

    /// <summary>Caps a single expiry sweep so a pathological backlog cannot spin forever.</summary>
    public int MaxSuppressionExpiryBatches { get; set; } = 100;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(DetectionBatchSize, 1, nameof(DetectionBatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(SuppressionExpiryBatchSize, 1, nameof(SuppressionExpiryBatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSuppressionExpiryBatches, 1, nameof(MaxSuppressionExpiryBatches));
    }
}
