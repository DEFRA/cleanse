using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Runs;

/// <summary>
/// Ends a run without sweeping, for when a rule crashed part way through. Issues keep whatever
/// status the last complete run left them with, and the group is free to start a fresh run.
/// </summary>
public sealed record AbandonRunCommand(string RunId, string Reason, Actor Actor) : ICommand<RuleRunDto>;

internal sealed class AbandonRunCommandHandler(IRuleRunStore runs, IAuditStore audit, TimeProvider clock)
    : ICommandHandler<AbandonRunCommand, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        AbandonRunCommand command,
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

        var now = clock.GetUtcNow();
        var abandoned = run.Abandon(command.Reason, now);
        if (abandoned.IsFailure)
        {
            return abandoned.ToFailure<RuleRunDto>();
        }

        await runs.SaveAsync(run, cancellationToken);
        await audit.AppendAsync(
            AuditEntry.ForRun(
                run.RunId,
                AuditEventKind.RunAbandoned,
                command.Actor,
                now,
                [new AuditChange(nameof(RuleRun.AbandonReason), null, run.AbandonReason)]),
            cancellationToken);

        return run.ToDto(now);
    }
}
