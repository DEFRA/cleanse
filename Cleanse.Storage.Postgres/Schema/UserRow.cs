using Cleanse.Core.Domain;

namespace Cleanse.Storage.Postgres.Schema;

internal sealed class UserRow
{
    public Guid Id { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public required string TenantId { get; set; }
    public required string ObjectId { get; set; }
    public required string ClientId { get; set; }
    public required string FullName { get; set; }
    public DateTimeOffset FirstLoggedInAtUtc { get; set; }
    public DateTimeOffset LastLoggedInAtUtc { get; set; }

    public KnownUser ToDomain() => new(
        Id, Issuer, Subject, TenantId, ObjectId, ClientId, FullName, FirstLoggedInAtUtc, LastLoggedInAtUtc);
}
