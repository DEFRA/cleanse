using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresUserStore(CleanseDbContext db) : IUserStore
{
    public async Task<KnownUser> UpsertLoginAsync(KnownUser login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        // RETURNING is part of the upsert, not a second read that can observe another login.
        var rows = await db.Users.FromSqlInterpolated($"""
            INSERT INTO users
                (id, issuer, subject, tenant_id, object_id, client_id, full_name,
                 first_logged_in_at_utc, last_logged_in_at_utc)
            VALUES
                ({login.Id}, {login.Issuer}, {login.Subject}, {login.TenantId}, {login.ObjectId},
                 {login.ClientId}, {login.FullName}, {login.FirstLoggedInAtUtc}, {login.LastLoggedInAtUtc})
            ON CONFLICT (issuer, client_id, subject) DO UPDATE SET
                tenant_id = EXCLUDED.tenant_id,
                object_id = EXCLUDED.object_id,
                full_name = EXCLUDED.full_name,
                last_logged_in_at_utc = GREATEST(users.last_logged_in_at_utc, EXCLUDED.last_logged_in_at_utc)
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);
        return rows.Single().ToDomain();
    }

    public async Task<KnownUser?> GetAsync(
        Guid id, string tenantId, string clientId, CancellationToken cancellationToken = default)
    {
        var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(
            u => u.Id == id && u.TenantId == tenantId && u.ClientId == clientId, cancellationToken);
        return row?.ToDomain();
    }

    public async Task<PagedResult<KnownUser>> SearchAsync(
        string tenantId, string clientId, PageRequest page, CancellationToken cancellationToken = default)
    {
        var query = db.Users.AsNoTracking().Where(u => u.TenantId == tenantId && u.ClientId == clientId);
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(u => u.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<KnownUser>([.. rows.Select(u => u.ToDomain())], page.PageNumber, page.PageSize, count);
    }
}
