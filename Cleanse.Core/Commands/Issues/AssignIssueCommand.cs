using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Issues;

/// <summary>Assigns, or with a null <see cref="AssignTo"/>, unassigns.</summary>
public sealed record AssignIssueCommand(
    IssueId IssueId,
    string? AssignTo,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class AssignIssueCommandHandler(IssueMutator mutator)
    : ICommandHandler<AssignIssueCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        AssignIssueCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.AssignTo(command.AssignTo, now),
            cancellationToken);
    }
}

public sealed record SetIssueNotesCommand(
    IssueId IssueId,
    string? Notes,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class SetIssueNotesCommandHandler(IssueMutator mutator)
    : ICommandHandler<SetIssueNotesCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        SetIssueNotesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.SetNotes(command.Notes, now),
            cancellationToken);
    }
}

public sealed record SetIssuePriorityCommand(
    IssueId IssueId,
    IssuePriority? Priority,
    Actor Actor,
    int? ExpectedVersion = null) : ICommand<IssueDto>;

internal sealed class SetIssuePriorityCommandHandler(IssueMutator mutator)
    : ICommandHandler<SetIssuePriorityCommand, IssueDto>
{
    public Task<Result<IssueDto>> HandleAsync(
        SetIssuePriorityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return mutator.MutateAsync(
            command.IssueId,
            command.ExpectedVersion,
            command.Actor,
            (issue, now) => issue.SetPriority(command.Priority, now),
            cancellationToken);
    }
}
