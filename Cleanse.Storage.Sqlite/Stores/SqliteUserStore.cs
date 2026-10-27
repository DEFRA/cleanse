using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteUserStore(CleanseSqliteDatabase database) : IUserStore
{
    private const string Columns =
        "id, issuer, subject, tenant_id, object_id, client_id, full_name, first_logged_in_at_utc, last_logged_in_at_utc";

    public async Task<KnownUser> UpsertLoginAsync(KnownUser login, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO users ({Columns})
            VALUES ($id, $issuer, $subject, $tenant, $object, $client, $name, $first, $last)
            ON CONFLICT (issuer, client_id, subject) DO UPDATE SET
                tenant_id = excluded.tenant_id,
                object_id = excluded.object_id,
                full_name = excluded.full_name,
                last_logged_in_at_utc = MAX(users.last_logged_in_at_utc, excluded.last_logged_in_at_utc)
            RETURNING {Columns}
            """;
        command.Bind("$id", login.Id.ToString());
        command.Bind("$issuer", login.Issuer);
        command.Bind("$subject", login.Subject);
        command.Bind("$tenant", login.TenantId);
        command.Bind("$object", login.ObjectId);
        command.Bind("$client", login.ClientId);
        command.Bind("$name", login.FullName);
        command.Bind("$first", login.FirstLoggedInAtUtc.ToText());
        command.Bind("$last", login.LastLoggedInAtUtc.ToText());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Read(reader);
    }

    public async Task<KnownUser?> GetAsync(
        Guid id, string tenantId, string clientId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM users WHERE id = $id AND tenant_id = $tenant AND client_id = $client";
        command.Bind("$id", id.ToString());
        BindScope(command, tenantId, clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<PagedResult<KnownUser>> SearchAsync(
        string tenantId, string clientId, PageRequest page, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM users WHERE tenant_id = $tenant AND client_id = $client";
        BindScope(count, tenantId, clientId);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM users WHERE tenant_id = $tenant AND client_id = $client
            ORDER BY id LIMIT $limit OFFSET $offset
            """;
        BindScope(command, tenantId, clientId);
        command.Bind("$limit", page.PageSize);
        command.Bind("$offset", page.Skip);
        var items = new List<KnownUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return new PagedResult<KnownUser>(items, page.PageNumber, page.PageSize, total);
    }

    private static void BindScope(SqliteCommand command, string tenantId, string clientId)
    {
        command.Bind("$tenant", tenantId);
        command.Bind("$client", clientId);
    }

    private static KnownUser Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        SqliteMappings.ToTimestamp(reader.GetString(7)), SqliteMappings.ToTimestamp(reader.GetString(8)));
}
