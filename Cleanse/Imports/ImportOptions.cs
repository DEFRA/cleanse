namespace Cleanse.Api.Imports;

public sealed class ImportOptions
{
    /// <summary>Local directory or S3 bucket, depending on the provider.</summary>
    public string? FileStoreRoot { get; set; }

    public string? S3BucketName { get; set; }

    /// <summary>Rows applied per command. Keeps a single transaction small on any provider.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Generous: the file is 8 MB today, and rejecting at the edge beats a partial read.</summary>
    public long MaxUploadBytes { get; set; } = 50 * 1024 * 1024;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a processing job may go without reporting before the reaper reclaims it. Must
    /// comfortably exceed the time one batch takes, or healthy jobs get abandoned mid-flight.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Caps the stored reconciliation report so a mis-scoped import cannot write 30,000 rows.</summary>
    public int MaxRejectionsRecorded { get; set; } = 1000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxUploadBytes, 1024, nameof(MaxUploadBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRejectionsRecorded, 0, nameof(MaxRejectionsRecorded));

        if (StaleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StaleAfter), StaleAfter, "Must be positive.");
        }
    }
}
