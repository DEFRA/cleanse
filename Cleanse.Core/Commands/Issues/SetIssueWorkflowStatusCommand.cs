using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Issues;

/// <summary>
/// Moves an issue along the human triage axis. Suppression is not reachable here because it
/// needs an expiry; use <see cref="SuppressIssueCommand"/>.
/// </summary>
public sealed record SetIssueWorkflowStatusCommand(
    IssueId IssueId,
    WorkflowStatus WorkflowStatus,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class SetIssueWorkflowStatusCommandHandler(IssueMutator mutator)
    : ICommandHandler<SetIssueWorkflowStatusCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        SetIssueWorkflowStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.ChangeWorkflowStatus(command.WorkflowStatus, now),
            cancellationToken);
    }
}

/// <summary>Temporarily hides an issue from triage until <see cref="ExpiresAtUtc"/>.</summary>
public sealed record SuppressIssueCommand(
    IssueId IssueId,
    DateTimeOffset ExpiresAtUtc,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class SuppressIssueCommandHandler(IssueMutator mutator)
    : ICommandHandler<SuppressIssueCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        SuppressIssueCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.Suppress(command.ExpiresAtUtc, now),
            cancellationToken);
    }
}

/// <summary>Lifts a suppression early, returning the issue to the triage queue.</summary>
public sealed record UnsuppressIssueCommand(
    IssueId IssueId,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class UnsuppressIssueCommandHandler(IssueMutator mutator)
    : ICommandHandler<UnsuppressIssueCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        UnsuppressIssueCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.Unsuppress(now),
            cancellationToken);
    }
}
