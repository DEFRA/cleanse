using Cleanse.Core.Domain;

namespace Cleanse.Core.Abstractions;

public interface IUserStore
{
    /// <summary>Atomically inserts or updates by (issuer, clientId, subject), retaining the original id and first login.</summary>
    Task<KnownUser> UpsertLoginAsync(KnownUser login, CancellationToken cancellationToken = default);

    Task<KnownUser?> GetAsync(
        Guid id, string tenantId, string clientId, CancellationToken cancellationToken = default);

    Task<PagedResult<KnownUser>> SearchAsync(
        string tenantId, string clientId, PageRequest page, CancellationToken cancellationToken = default);
}
