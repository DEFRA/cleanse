namespace Cleanse.Rules.Krds.Snapshots;

/// <summary>Uses a SQLite snapshot already on disk. The local development path.</summary>
internal sealed class LocalFileSnapshotSource(string filePath) : IKrdsSnapshotSource
{
    public string Description => $"local file {filePath}";

    public Task<KrdsSnapshot> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"The KRDS snapshot '{filePath}' does not exist.", filePath);
        }

        // ownsFile: false. This is the developer's own copy, not scratch space.
        return Task.FromResult(new KrdsSnapshot(filePath, ownsFile: false, Description));
    }
}

/// <summary>
/// Stands in when no usable snapshot configuration was supplied.
/// </summary>
/// <remarks>
/// Registering this rather than throwing during startup is deliberate: build-time OpenAPI
/// generation runs the composition root, so an eager throw would break the build on any machine
/// without deployment configuration. The failure lands on the run that needed it instead.
/// </remarks>
internal sealed class UnconfiguredSnapshotSource(string reason) : IKrdsSnapshotSource
{
    public string Description => "unconfigured";

    public Task<KrdsSnapshot> AcquireAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"The KRDS snapshot source is not configured. {reason}");
}
