using System.Diagnostics.CodeAnalysis;
using Cleanse.Utils.Auditing;
using Elastic.Serilog.Enrichers.Web;
using Serilog;
using Serilog.Events;

namespace Cleanse.Utils.Logging;

public static class CdpLogging
{
    [ExcludeFromCodeCoverage]
    public static void Configuration(HostBuilderContext ctx, LoggerConfiguration config)
    {
        var httpAccessor = ctx.Configuration.Get<HttpContextAccessor>();
        var traceIdHeader = ctx.Configuration.GetValue<string>("TraceHeader");

        ApplyMinimumLevels(ctx.Configuration.GetSection("Serilog:MinimumLevel"), config);

        var mainLogger = new LoggerConfiguration()
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.WithEcsHttpContext(httpAccessor!)
            .Enrich.FromLogContext()
            .Filter.With<AuditLogger.Filters.ExcludeAuditEvents>()
            .CreateLogger();

        if (traceIdHeader != null)
        {
            config.Enrich.WithCorrelationId(traceIdHeader);
        }

        var auditLogger = AuditLogger.CreateAuditLogger();

        config
            .WriteTo.Logger(mainLogger)
            .WriteTo.Logger(auditLogger);
    }

    /// <summary>
    /// Serilog applies MinimumLevel overrides when a logger is created for a source context, and
    /// that happens on this root logger, never on the sub-loggers it writes to. Configured only on
    /// the main sub-logger, as ReadFrom.Configuration does, the overrides would be ignored, so they
    /// are applied here too. The default may be lowered but is never raised above Information:
    /// audit events pass through this root on their way to the audit logger, and configuration
    /// must not be able to drop them.
    /// </summary>
    internal static void ApplyMinimumLevels(IConfigurationSection section, LoggerConfiguration config)
    {
        var configuredDefault = section.Value ?? section["Default"];
        if (Enum.TryParse<LogEventLevel>(configuredDefault, ignoreCase: true, out var level)
            && level < LogEventLevel.Information)
        {
            config.MinimumLevel.Is(level);
        }

        foreach (var source in section.GetSection("Override").GetChildren())
        {
            if (Enum.TryParse<LogEventLevel>(source.Value, ignoreCase: true, out var sourceLevel))
            {
                config.MinimumLevel.Override(source.Key, sourceLevel);
            }
        }
    }
}