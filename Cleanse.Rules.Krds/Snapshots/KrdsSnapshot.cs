namespace Cleanse.Rules.Krds.Snapshots;

/// <summary>
/// A SQLite snapshot on local disk, ready to be opened.
/// </summary>
/// <remarks>
/// A file path rather than a stream because SQLite needs random access to a real file; it
/// cannot read a snapshot from a <see cref="Stream"/>. Disposing deletes the file only when
/// this handle created it, so a developer's local copy is never destroyed.
/// </remarks>
public sealed class KrdsSnapshot(string filePath, bool ownsFile, string description) : IAsyncDisposable
{
    public string FilePath { get; } = filePath;

    public string Description { get; } = description;

    public ValueTask DisposeAsync()
    {
        if (ownsFile)
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (IOException)
            {
                // A leftover file in scratch space is not worth failing a completed run over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Obtains the SQLite snapshot a run evaluates.</summary>
public interface IKrdsSnapshotSource
{
    string Description { get; }

    Task<KrdsSnapshot> AcquireAsync(CancellationToken cancellationToken = default);
}
