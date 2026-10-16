using Cleanse.Core.Abstractions;

namespace Cleanse.Storage.InMemory;

/// <summary>
/// Always healthy, because the store is a dictionary in this process. Reported honestly so an
/// operator can see at a glance that nothing is being persisted anywhere.
/// </summary>
public sealed class InMemoryStorageDiagnostics(InMemoryIssueStore issues) : IStorageDiagnostics
{
    public string ProviderName => "InMemory";

    public string? Target => "this process only, nothing is persisted";

    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProbeResult.Ok(0, $"{issues.Count} issues held in memory"));
    }
}
