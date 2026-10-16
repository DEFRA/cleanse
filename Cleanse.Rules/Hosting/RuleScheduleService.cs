using Cleanse.Core;
using Cleanse.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Hosting;

/// <summary>
/// Starts a run once a day at the configured UTC time.
/// </summary>
/// <remarks>
/// Every instance of the service schedules independently. That is deliberate rather than
/// overlooked: the loser of the race is refused by the single-run-per-group constraint and logs
/// the fact, which is cheaper and less to go wrong than leader election.
/// </remarks>
internal sealed class RuleScheduleService(
    IServiceScopeFactory scopeFactory,
    CleanseRulesOptions options,
    TimeProvider clock,
    ILogger<RuleScheduleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Rule schedule active: {GroupId} runs daily at {Time} UTC",
            options.GroupId, options.DailyRunAtUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextRun(clock.GetUtcNow());
            logger.LogDebug("Next scheduled rule run in {Delay}", delay);

            try
            {
                await Task.Delay(delay, clock, stoppingToken);
                await TriggerAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled rule run could not be started");
            }
        }
    }

    internal TimeSpan DelayUntilNextRun(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) + options.DailyRunAtUtc;
        var next = today > now ? today : today.AddDays(1);
        return next - now;
    }

    private async Task TriggerAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IRuleEngineLauncher>();

        var started = await launcher.TryStartAsync(Actor.System, cancellationToken);
        if (started.IsSuccess)
        {
            logger.LogInformation("Scheduled rule run {RunId} started", started.Value.RunId);
            return;
        }

        if (started.Error.Code == ErrorCodes.RunAlreadyInProgress)
        {
            logger.LogInformation("Skipping scheduled run: {Reason}", started.Error.Message);
            return;
        }

        logger.LogError("Scheduled rule run was refused: {Error}", started.Error);
    }
}
