namespace Cleanse.Rules.Krds;

public enum KrdsSnapshotProvider
{
    /// <summary>Read a SQLite file already on disk. The local development path.</summary>
    LocalFile,

    /// <summary>Download the latest snapshot from the KRDS Data Bridge. The deployed path.</summary>
    DataBridge
}

public sealed class KrdsOptions
{
    public string Provider { get; set; } = nameof(KrdsSnapshotProvider.DataBridge);

    /// <summary>Path to a KRDS SQLite snapshot. Required when the provider is LocalFile.</summary>
    public string? LocalFilePath { get; set; }

    /// <summary>Base URL of the KRDS Data Bridge, with no trailing path.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The bare Data Bridge key. The scheme is prepended when the header is set, so no scheme
    /// belongs in configuration.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// How long the presigned download URL should stay valid. The Data Bridge caps this at 60.
    /// </summary>
    public int DownloadUrlExpiryMinutes { get; set; } = 30;

    /// <summary>Generous by default: the snapshot runs to the better part of a gigabyte.</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Where downloaded snapshots are written. Defaults to the system temp folder.</summary>
    public string? ScratchDirectory { get; set; }

    public KrdsSnapshotProvider? ResolveProvider() =>
        Enum.TryParse<KrdsSnapshotProvider>(Provider, ignoreCase: true, out var provider) ? provider : null;

    /// <summary>
    /// Why this configuration cannot be used, or null when it can.
    /// </summary>
    /// <remarks>
    /// Returned rather than thrown. Build-time OpenAPI generation executes the composition root,
    /// so throwing here for missing deployment configuration would break the build rather than
    /// just the run.
    /// </remarks>
    public string? Describe()
    {
        var provider = ResolveProvider();
        if (provider is null)
        {
            return $"Rules:Krds:Provider must be '{nameof(KrdsSnapshotProvider.LocalFile)}' or " +
                   $"'{nameof(KrdsSnapshotProvider.DataBridge)}'; found '{Provider}'.";
        }

        if (provider is KrdsSnapshotProvider.LocalFile && string.IsNullOrWhiteSpace(LocalFilePath))
        {
            return "Rules:Krds:LocalFilePath must be set when the provider is LocalFile.";
        }

        if (provider is not KrdsSnapshotProvider.DataBridge)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            return "Rules:Krds:BaseUrl must be set when the provider is DataBridge.";
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            return $"Rules:Krds:BaseUrl is not an absolute URL: '{BaseUrl}'.";
        }

        return string.IsNullOrWhiteSpace(ApiKey)
            ? "Rules:Krds:ApiKey must be set when the provider is DataBridge."
            : null;
    }
}
