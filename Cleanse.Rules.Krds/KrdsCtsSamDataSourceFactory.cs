using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Krds.Snapshots;

namespace Cleanse.Rules.Krds;

/// <summary>Acquires a snapshot and opens a reader over it, for the duration of one run.</summary>
internal sealed class KrdsCtsSamDataSourceFactory(IKrdsSnapshotSource snapshots) : ICtsSamDataSourceFactory
{
    public string Description => snapshots.Description;

    public async Task<ICtsSamDataSource> OpenAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await snapshots.AcquireAsync(cancellationToken);

        try
        {
            return new KrdsSqliteDataSource(snapshot);
        }
        catch
        {
            await snapshot.DisposeAsync();
            throw;
        }
    }
}
