using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Maintenance;

/// <summary>
/// Irreversibly deletes Cleanse data. Unlike the end-of-run sweep, which only marks issues
/// inactive, nothing survives this: triage decisions, caseworker notes and the audit trail go
/// with the issues. Core does not decide who may ask; the host guards the entry point.
/// </summary>
public sealed record ResetDataCommand(string? GroupId, Actor Actor) : ICommand<ResetResultDto>;

internal sealed class ResetDataCommandHandler(IDataResetStore store, TimeProvider clock)
    : ICommandHandler<ResetDataCommand, ResetResultDto>
{
    public async Task<Result<ResetResultDto>> HandleAsync(
        ResetDataCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var groupId = string.IsNullOrWhiteSpace(command.GroupId) ? null : command.GroupId.Trim();
        var counts = await store.ResetAsync(
            groupId, command.Actor.Id, clock.GetUtcNow(), cancellationToken);

        return new ResetResultDto(
            groupId ?? ResetResultDto.AllGroups,
            counts.Issues,
            counts.IssueHistoryEntries,
            counts.AuditEntries,
            counts.RuleRuns,
            counts.ImportJobs,
            counts.ImportRejections);
    }
}
