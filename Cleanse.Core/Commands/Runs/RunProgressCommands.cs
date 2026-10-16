using System.Globalization;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Runs;

/// <summary>
/// Records where a run has got to. Deliberately cheap: it writes the run row and nothing else,
/// because an engine calls it on every flushed batch.
/// </summary>
public sealed record ReportRunProgressCommand(
    string RunId,
    string? CurrentStage,
    int RecordsProcessed,
    int RecordsTotal) : ICommand<RuleRunDto>;

internal sealed class ReportRunProgressCommandHandler(IRuleRunStore runs, TimeProvider clock)
    : ICommandHandler<ReportRunProgressCommand, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        ReportRunProgressCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RunId))
        {
            return DomainErrors.Validation("A run id is required.");
        }

        if (command.RecordsProcessed < 0 || command.RecordsTotal < 0)
        {
            return DomainErrors.Validation("Record counts cannot be negative.");
        }

        var run = await runs.GetAsync(command.RunId.Trim(), cancellationToken);
        if (run is null)
        {
            return DomainErrors.RunNotFound(command.RunId);
        }

        var now = clock.GetUtcNow();
        var reported = run.ReportProgress(command.CurrentStage, command.RecordsProcessed, command.RecordsTotal, now);
        if (reported.IsFailure)
        {
            return reported.ToFailure<RuleRunDto>();
        }

        await runs.SaveAsync(run, cancellationToken);
        return run.ToDto(now);
    }
}

/// <summary>
/// Asks a running run to stop. The run keeps running until the engine next polls, so that it
/// stops at a batch boundary rather than mid-write.
/// </summary>
public sealed record RequestRunCancellationCommand(string RunId, Actor Actor) : ICommand<RuleRunDto>;

internal sealed class RequestRunCancellationCommandHandler(
    IRuleRunStore runs,
    IAuditStore audit,
    TimeProvider clock)
    : ICommandHandler<RequestRunCancellationCommand, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        RequestRunCancellationCommand command,
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

        var alreadyRequested = run.CancellationRequested;
        var now = clock.GetUtcNow();
        var requested = run.RequestCancellation(now);
        if (requested.IsFailure)
        {
            return requested.ToFailure<RuleRunDto>();
        }

        if (!alreadyRequested)
        {
            await runs.SaveAsync(run, cancellationToken);
            await audit.AppendAsync(
                AuditEntry.ForRun(
                    run.RunId,
                    AuditEventKind.RunCancellationRequested,
                    command.Actor,
                    now,
                    [new AuditChange(nameof(RuleRun.CancellationRequested), "false", "true")]),
                cancellationToken);
        }

        return run.ToDto(now);
    }
}

/// <summary>Ends a run that was asked to stop. No sweep, so nothing is wrongly deactivated.</summary>
public sealed record CancelRunCommand(string RunId, string? Reason, Actor Actor) : ICommand<RuleRunDto>;

internal sealed class CancelRunCommandHandler(IRuleRunStore runs, IAuditStore audit, TimeProvider clock)
    : ICommandHandler<CancelRunCommand, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        CancelRunCommand command,
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
        var cancelled = run.Cancel(command.Reason, now);
        if (cancelled.IsFailure)
        {
            return cancelled.ToFailure<RuleRunDto>();
        }

        await runs.SaveAsync(run, cancellationToken);
        await audit.AppendAsync(
            AuditEntry.ForRun(
                run.RunId,
                AuditEventKind.RunCancelled,
                command.Actor,
                now,
                [new AuditChange(nameof(RuleRun.AbandonReason), null, run.AbandonReason)]),
            cancellationToken);

        return run.ToDto(now);
    }
}

/// <summary>
/// Ends a run that threw. <paramref name="Detail"/> is the full technical dump, kept on the run
/// so a failure can be diagnosed from the API without going to the log aggregator.
/// </summary>
public sealed record FailRunCommand(string RunId, string Message, string? Detail, Actor Actor) : ICommand<RuleRunDto>;

internal sealed class FailRunCommandHandler(IRuleRunStore runs, IAuditStore audit, TimeProvider clock)
    : ICommandHandler<FailRunCommand, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        FailRunCommand command,
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
        var failed = run.Fail(command.Message, command.Detail, now);
        if (failed.IsFailure)
        {
            return failed.ToFailure<RuleRunDto>();
        }

        await runs.SaveAsync(run, cancellationToken);
        await audit.AppendAsync(
            AuditEntry.ForRun(
                run.RunId,
                AuditEventKind.RunFailed,
                command.Actor,
                now,
                [new AuditChange(nameof(RuleRunFailure.Message), null, run.Failure?.Message)]),
            cancellationToken);

        return run.ToDto(now);
    }
}

/// <summary>
/// Abandons running runs whose heartbeat has gone quiet. A process that dies mid-run would
/// otherwise hold its group's single-run slot forever.
/// </summary>
public sealed record ReapStalledRunsCommand(TimeSpan StaleAfter, int Limit = 50) : ICommand<int>;

internal sealed class ReapStalledRunsCommandHandler(IRuleRunStore runs, IAuditStore audit, TimeProvider clock)
    : ICommandHandler<ReapStalledRunsCommand, int>
{
    public async Task<Result<int>> HandleAsync(
        ReapStalledRunsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.StaleAfter <= TimeSpan.Zero)
        {
            return DomainErrors.Validation("The stale threshold must be positive.");
        }

        var now = clock.GetUtcNow();
        var stalled = await runs.FindStalledAsync(now - command.StaleAfter, command.Limit, cancellationToken);

        var reaped = 0;
        foreach (var run in stalled)
        {
            var minutes = command.StaleAfter.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture);
            var abandoned = run.Abandon($"No heartbeat for {minutes} minutes; presumed dead.", now);
            if (abandoned.IsFailure)
            {
                continue;
            }

            await runs.SaveAsync(run, cancellationToken);
            await audit.AppendAsync(
                AuditEntry.ForRun(
                    run.RunId,
                    AuditEventKind.RunAbandoned,
                    Actor.System,
                    now,
                    [new AuditChange(nameof(RuleRun.AbandonReason), null, run.AbandonReason)]),
                cancellationToken);
            reaped++;
        }

        return reaped;
    }
}
