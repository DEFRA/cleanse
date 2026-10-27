using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

public sealed class InMemoryUserStore : IUserStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Issuer, string ClientId, string Subject), KnownUser> _users = [];

    public Task<KnownUser> UpsertLoginAsync(KnownUser login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (login.Issuer, login.ClientId, login.Subject);
            if (_users.TryGetValue(key, out var existing))
            {
                login = login with
                {
                    Id = existing.Id,
                    FirstLoggedInAtUtc = existing.FirstLoggedInAtUtc,
                    LastLoggedInAtUtc = login.LastLoggedInAtUtc > existing.LastLoggedInAtUtc
                        ? login.LastLoggedInAtUtc : existing.LastLoggedInAtUtc
                };
            }

            _users[key] = login;
            return Task.FromResult(login);
        }
    }

    public Task<KnownUser?> GetAsync(
        Guid id, string tenantId, string clientId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_users.Values.FirstOrDefault(
                u => u.Id == id && u.TenantId == tenantId && u.ClientId == clientId));
        }
    }

    public Task<PagedResult<KnownUser>> SearchAsync(
        string tenantId, string clientId, PageRequest page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var scoped = _users.Values.Where(u => u.TenantId == tenantId && u.ClientId == clientId)
                .OrderBy(u => u.Id).ToArray();
            return Task.FromResult(new PagedResult<KnownUser>(
                [.. scoped.Skip(page.Skip).Take(page.PageSize)], page.PageNumber, page.PageSize, scoped.LongLength));
        }
    }
}
