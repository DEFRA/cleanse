namespace Cleanse.Core.Domain;

/// <summary>A service-attested login identity, independent of issue and import lifetimes.</summary>
public sealed record KnownUser(
    Guid Id,
    string Issuer,
    string Subject,
    string TenantId,
    string ObjectId,
    string ClientId,
    string FullName,
    DateTimeOffset FirstLoggedInAtUtc,
    DateTimeOffset LastLoggedInAtUtc);
