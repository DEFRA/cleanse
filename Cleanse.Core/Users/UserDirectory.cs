using System.ComponentModel.DataAnnotations;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Users;

internal sealed class UserDirectory(IUserStore users, TimeProvider clock) : IUserDirectory
{
    public async Task<Result<UserDto>> RecordLoginAsync(
        RecordUserLoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, true))
        {
            return DomainErrors.Validation(string.Join(" ", errors.Select(e => e.ErrorMessage)));
        }

        var now = clock.GetUtcNow();
        var user = await users.UpsertLoginAsync(new KnownUser(
            Guid.NewGuid(), request.Issuer, request.Subject, request.TenantId, request.ObjectId,
            request.ClientId, request.FullName, now, now), cancellationToken);
        return ToDto(user);
    }

    public async Task<Result<PagedResult<UserDto>>> SearchAsync(
        string tenantId, string clientId, PageRequest page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 64 ||
            string.IsNullOrWhiteSpace(clientId) || clientId.Length > 64)
        {
            return DomainErrors.Validation("tenantId and clientId are required and must be at most 64 characters.");
        }

        var result = await users.SearchAsync(tenantId, clientId, page, cancellationToken);
        return result.Map(ToDto);
    }

    private static UserDto ToDto(KnownUser user) => new(
        user.Id, user.Issuer, user.Subject, user.TenantId, user.ObjectId, user.ClientId,
        user.FullName, user.FirstLoggedInAtUtc, user.LastLoggedInAtUtc);
}
