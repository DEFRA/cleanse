using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Hosting;

/// <summary>
/// Abandons runs whose heartbeat has gone quiet, so a process that died mid-run does not hold
/// its group's single run slot indefinitely.
/// </summary>
internal sealed class StalledRunReaperService(
    IServiceScopeFactory scopeFactory,
    CleanseRulesOptions options,
    TimeProvider clock,
    ILogger<StalledRunReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sweeping at a fraction of the threshold keeps the worst-case wait bounded without
        // polling the database more often than the threshold makes meaningful.
        var interval = TimeSpan.FromTicks(Math.Max(options.StaleAfter.Ticks / 3, TimeSpan.TicksPerMinute));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICleanseDispatcher>();

                var result = await dispatcher.SendAsync(
                    new ReapStalledRunsCommand(options.StaleAfter), stoppingToken);

                if (result.IsSuccess && result.Value > 0)
                {
                    logger.LogWarning("Abandoned {Count} stalled rule run(s)", result.Value);
                }
                else if (result.IsFailure)
                {
                    logger.LogError("Could not reap stalled runs: {Error}", result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stalled run reaper failed; will retry");
            }

            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
