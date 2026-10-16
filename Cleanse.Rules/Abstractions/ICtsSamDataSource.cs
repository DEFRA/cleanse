using Cleanse.Rules.Domain;

namespace Cleanse.Rules.Abstractions;

/// <summary>
/// Reads the CTS and SAM holding data a run evaluates. One instance per run; disposing it
/// releases whatever the implementation acquired, including any downloaded snapshot.
/// </summary>
public interface ICtsSamDataSource : IAsyncDisposable
{
    /// <summary>
    /// How many records the pass will visit. Known before streaming starts so that a run can
    /// report a percentage and a projected completion rather than just a running total.
    /// </summary>
    Task<int> CountRecordsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every in-scope CTS open location with its SAM holding attached, streamed rather than
    /// materialised so that memory does not scale with the dataset.
    /// </summary>
    IAsyncEnumerable<CtsSamRecord> StreamRecordsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Acquires a data source for a run. Implementations own snapshot retrieval.</summary>
public interface ICtsSamDataSourceFactory
{
    /// <summary>A human-readable description of where the data came from, for the run log.</summary>
    string Description { get; }

    Task<ICtsSamDataSource> OpenAsync(CancellationToken cancellationToken = default);
}
