using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Issues;

public sealed record AssignKnownUserCommand(
    IssueId IssueId,
    Guid? UserId,
    string? TenantId,
    string? ClientId,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class AssignKnownUserCommandHandler(IUserStore users, IssueMutator mutator)
    : ICommandHandler<AssignKnownUserCommand, IssueDto>
{
    public async Task<Result<IssueDto>> HandleAsync(
        AssignKnownUserCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TenantId) || command.TenantId.Length > 64 ||
            string.IsNullOrWhiteSpace(command.ClientId) || command.ClientId.Length > 64)
        {
            return DomainErrors.Validation("tenantId and clientId are required and must be at most 64 characters.");
        }

        KnownUser? user = null;
        if (command.UserId is { } id)
        {
            user = await users.GetAsync(id, command.TenantId, command.ClientId, cancellationToken);
            if (user is null)
            {
                return DomainErrors.Validation("userId must identify a known user in the supplied tenant and client scope.");
            }
        }

        return await mutator.MutateAsync(
            command.IssueId, command.ExpectedVersion, command.Actor,
            (issue, now) => issue.AssignKnownUser(user, now), cancellationToken);
    }
}
