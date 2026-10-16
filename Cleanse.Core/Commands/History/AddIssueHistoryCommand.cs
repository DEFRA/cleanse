using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.History;

/// <summary>
/// Records a human case action against an issue. Separate from the audit trail: this is what
/// somebody did about the anomaly, not what the system did to the record.
/// </summary>
public sealed record AddIssueHistoryCommand(
    IssueId IssueId,
    HistoryActionType ActionType,
    string Comments,
    Actor Actor) : ICommand<IssueHistoryEntryDto>;

internal sealed class AddIssueHistoryCommandHandler(
    IIssueStore issues,
    IIssueHistoryStore history,
    IAuditStore audit,
    TimeProvider clock)
    : ICommandHandler<AddIssueHistoryCommand, IssueHistoryEntryDto>
{
    public async Task<Result<IssueHistoryEntryDto>> HandleAsync(
        AddIssueHistoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        if (string.IsNullOrWhiteSpace(command.Comments))
        {
            return DomainErrors.Validation("A history entry needs a comment.");
        }

        var issue = await issues.GetAsync(command.IssueId, cancellationToken);
        if (issue is null)
        {
            return DomainErrors.IssueNotFound(command.IssueId.Value);
        }

        var now = clock.GetUtcNow();
        var entry = IssueHistoryEntry.Create(
            command.IssueId, command.ActionType, command.Comments, command.Actor, now);

        await history.AddAsync(entry, cancellationToken);
        await audit.AppendAsync(
            AuditEntry.ForIssue(
                command.IssueId,
                new IssueChange(
                    AuditEventKind.IssueHistoryAdded,
                    [new AuditChange(nameof(IssueHistoryEntry.ActionType), null, entry.ActionType.ToString())]),
                command.Actor,
                now),
            cancellationToken);

        return entry.ToDto();
    }
}
