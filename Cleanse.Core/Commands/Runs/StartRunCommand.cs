using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Runs;

/// <summary>
/// Opens a run for a group. Supply <see cref="RunId"/> when the scheduler needs to control the
/// identity; leave it null to have a time-ordered one generated.
/// </summary>
public sealed record StartRunCommand(string GroupId, Actor Actor, string? RunId = null) : ICommand<RuleRunDto>;

internal sealed class StartRunCommandHandler(IRuleRunStore runs, IAuditStore audit, TimeProvider clock)
    : ICommandHandler<StartRunCommand, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        StartRunCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        if (string.IsNullOrWhiteSpace(command.GroupId))
        {
            return DomainErrors.Validation("A run must be scoped to a group.");
        }

        var groupId = command.GroupId.Trim();

        // One run per group at a time, otherwise two overlapping runs would sweep each other's work.
        var inFlight = await runs.GetActiveForGroupAsync(groupId, cancellationToken);
        if (inFlight is not null)
        {
            return DomainErrors.RunAlreadyInProgress(groupId, inFlight.RunId);
        }

        var now = clock.GetUtcNow();
        var runId = string.IsNullOrWhiteSpace(command.RunId)
            ? Guid.CreateVersion7(now).ToString("n")
            : command.RunId.Trim();

        if (await runs.GetAsync(runId, cancellationToken) is not null)
        {
            return DomainErrors.Validation($"Run '{runId}' already exists.");
        }

        var run = RuleRun.Start(runId, groupId, now);
        await runs.SaveAsync(run, cancellationToken);
        await audit.AppendAsync(
            AuditEntry.ForRun(
                runId,
                AuditEventKind.RunStarted,
                command.Actor,
                now,
                [new AuditChange(nameof(RuleRun.GroupId), null, groupId)]),
            cancellationToken);

        return run.ToDto(now);
    }
}
