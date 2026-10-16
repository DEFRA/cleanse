using Cleanse.Rules.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Hosting;

/// <summary>Runs queued rule runs, one at a time, for the lifetime of the process.</summary>
internal sealed class RuleRunExecutorService(
    RuleRunQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<RuleRunExecutorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<CtsSamRuleEngine>();
                await engine.ExecuteAsync(request.RunId, request.Actor, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The engine records its own failures; reaching here means recording failed too.
                logger.LogError(ex, "Rule run {RunId} could not be executed", request.RunId);
            }
        }
    }
}
