using System.Globalization;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Runs;

/// <summary>
/// Closes a run and sweeps the group: every active issue the run did not re-detect becomes
/// inactive. Only call this once every rule in the group has reported, because the sweep is
/// group-wide and cannot tell a fixed anomaly from a rule that never ran.
/// </summary>
public sealed record CompleteRunCommand(string RunId, Actor Actor) : ICommand<RuleRunDto>;

internal sealed class CompleteRunCommandHandler(
    IRuleRunStore runs,
    IIssueStore issues,
    IAuditStore audit,
    TimeProvider clock)
    : ICommandHandler<CompleteRunCommand, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        CompleteRunCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        if (string.IsNullOrWhiteSpace(command.RunId))
        {
            return DomainErrors.Validation("A run id is required.");
        }

        var run = await runs.GetAsync(command.RunId.Trim(), cancellationToken);
        if (run is null)
        {
            return DomainErrors.RunNotFound(command.RunId);
        }

        if (!run.IsRunning)
        {
            return DomainErrors.RunNotRunning(run.RunId, run.State.ToString().ToLowerInvariant());
        }

        var now = clock.GetUtcNow();
        var deactivated = await issues.DeactivateStaleAsync(run.GroupId, run.RunId, now, cancellationToken);

        var completed = run.Complete(deactivated, now);
        if (completed.IsFailure)
        {
            return completed.ToFailure<RuleRunDto>();
        }

        await runs.SaveAsync(run, cancellationToken);

        // One run-scoped entry rather than one per issue: a sweep can touch six figures of rows.
        await audit.AppendAsync(
            AuditEntry.ForRun(
                run.RunId,
                AuditEventKind.RunCompleted,
                command.Actor,
                now,
                [
                    new AuditChange(
                        nameof(RuleRunCounters.IssuesDeactivated),
                        null,
                        deactivated.ToString(CultureInfo.InvariantCulture))
                ]),
            cancellationToken);

        return run.ToDto(now);
    }
}
