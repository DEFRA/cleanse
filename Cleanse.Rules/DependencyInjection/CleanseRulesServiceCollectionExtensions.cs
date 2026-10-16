using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Engine;
using Cleanse.Rules.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cleanse.Rules.DependencyInjection;

public static class CleanseRulesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the rules engine. A data source must be registered separately: the engine has
    /// no opinion about where a snapshot comes from. Background workers only start when
    /// <see cref="CleanseRulesOptions.Enabled"/> is set, so a test host never fires a run.
    /// </summary>
    public static IServiceCollection AddCleanseRules(
        this IServiceCollection services,
        CleanseRulesOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RuleRunQueue>();

        foreach (var rule in CleanseRuleRegistry.CreateDefaultRules())
        {
            services.AddSingleton<ICleanseRule>(rule);
        }

        services.TryAddSingleton<CleanseRuleRegistry>();
        services.TryAddScoped<CtsSamRuleEngine>();
        services.TryAddScoped<IRuleEngineLauncher, RuleEngineLauncher>();

        if (options.Enabled)
        {
            services.AddHostedService<RuleRunExecutorService>();
            services.AddHostedService<RuleScheduleService>();
            services.AddHostedService<StalledRunReaperService>();
        }

        return services;
    }
}
