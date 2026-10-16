using Cleanse.Rules;
using Cleanse.Rules.DependencyInjection;
using Cleanse.Rules.Engine;
using Cleanse.Rules.Krds;

namespace Cleanse.RulesEngine;

/// <summary>The <c>Rules</c> configuration section.</summary>
public sealed class RulesSectionOptions
{
    /// <summary>
    /// Whether the nightly schedule and background workers start. Off by default so that a test
    /// host, or a build-time OpenAPI generation pass, never fires a run.
    /// </summary>
    public bool Enabled { get; set; }

    public string GroupId { get; set; } = CleanseRuleRegistry.CtsSamGroupId;

    public TimeSpan DailyRunAtUtc { get; set; } = TimeSpan.FromHours(1);

    public int BatchSize { get; set; } = 500;

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(15);

    public KrdsOptions Krds { get; set; } = new();

    public CleanseRulesOptions ToEngineOptions() => new()
    {
        Enabled = Enabled,
        GroupId = GroupId,
        DailyRunAtUtc = DailyRunAtUtc,
        BatchSize = BatchSize,
        StaleAfter = StaleAfter
    };
}

public static class RuleRegistration
{
    /// <summary>
    /// Registers the rules engine and its KRDS data source.
    /// </summary>
    /// <remarks>
    /// Misconfiguration is recorded as a startup warning rather than thrown. Build-time OpenAPI
    /// generation executes this composition root, so an eager throw for missing deployment
    /// configuration would break the build on any machine that has none.
    /// </remarks>
    public static IServiceCollection AddCleanseRulesEngine(
        this IServiceCollection services,
        RulesSectionOptions options,
        StartupWarnings warnings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(warnings);

        services.AddCleanseRules(options.ToEngineOptions());

        var problem = services.AddKrdsDataSource(options.Krds);
        if (problem is not null)
        {
            warnings.Add($"The rules engine has no usable data source and cannot run. {problem}");
        }

        if (!options.Enabled)
        {
            warnings.Add(
                "Rules:Enabled is false. Neither the nightly schedule nor the background " +
                "executor is running, so POST /v1/runs/trigger will be refused with 503.");
        }

        return services;
    }
}
