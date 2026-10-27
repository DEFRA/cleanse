using System.ComponentModel.DataAnnotations;

namespace Cleanse.Core.Contracts;

/// <summary>Identity claims attested by cleanse-app after validating an Entra login, never browser-supplied claims.</summary>
public sealed record RecordUserLoginRequest
{
    [Required, StringLength(512)]
    public required string Issuer { get; init; }

    [Required, StringLength(256)]
    public required string Subject { get; init; }

    [Required, StringLength(64)]
    public required string TenantId { get; init; }

    [Required, StringLength(64)]
    public required string ObjectId { get; init; }

    [Required, StringLength(64)]
    public required string ClientId { get; init; }

    [Required, StringLength(256)]
    public required string FullName { get; init; }
}

/// <summary>A known user and their server-recorded UTC login timestamps.</summary>
public sealed record UserDto(
    Guid Id,
    string Issuer,
    string Subject,
    string TenantId,
    string ObjectId,
    string ClientId,
    string FullName,
    DateTimeOffset FirstLoggedInAtUtc,
    DateTimeOffset LastLoggedInAtUtc);
