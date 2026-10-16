using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Hosting;

/// <summary>Starts a run and hands it to the background executor.</summary>
public interface IRuleEngineLauncher
{
    /// <summary>
    /// Opens a run and queues it. Returns immediately with the run id; the work happens on a
    /// background worker. Fails with <see cref="ErrorCodes.RunAlreadyInProgress"/> when the
    /// group already has a run in flight.
    /// </summary>
    Task<Result<RuleRunDto>> TryStartAsync(Actor actor, CancellationToken cancellationToken = default);
}

internal sealed class RuleEngineLauncher(
    ICleanseDispatcher dispatcher,
    RuleRunQueue queue,
    CleanseRulesOptions options,
    ILogger<RuleEngineLauncher> logger) : IRuleEngineLauncher
{
    public async Task<Result<RuleRunDto>> TryStartAsync(
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // Without the executor there is nothing to drain the queue, so an accepted run would sit
        // in Running forever and hold the group's only run slot with no reaper to clear it.
        if (!options.Enabled)
        {
            logger.LogWarning("Refused a run for group {GroupId}: the rules engine is disabled", options.GroupId);
            return DomainErrors.RulesEngineDisabled();
        }

        // Opening the run first is what makes concurrency safe: the store refuses a second
        // running run for the group, so the race is settled before any work is queued.
        var started = await dispatcher.SendAsync(
            new StartRunCommand(options.GroupId, actor), cancellationToken);

        if (started.IsFailure)
        {
            return started;
        }

        var run = started.Value;
        queue.Enqueue(new RuleRunRequest(run.RunId, actor));
        logger.LogInformation("Queued rule run {RunId} for group {GroupId}", run.RunId, run.GroupId);

        return started;
    }
}
