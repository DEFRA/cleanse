using Cleanse.Core.Contracts;

namespace Cleanse.Core.Abstractions;

public interface IUserDirectory
{
    Task<Result<UserDto>> RecordLoginAsync(
        RecordUserLoginRequest request, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<UserDto>>> SearchAsync(
        string tenantId, string clientId, PageRequest page, CancellationToken cancellationToken = default);
}
