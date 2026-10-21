using Cleanse.Api.Test.Support;
using Cleanse.Utils.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cleanse.Api.Test;

/// <summary>
/// CdpLogging builds the main logger from the Serilog section alone, so the console sink declared
/// in appsettings.json is the service's only log output. It was once dropped without anyone
/// noticing: the service still started and answered, and simply wrote nothing to OpenSearch.
/// </summary>
public class LoggingConfigurationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void The_service_writes_ecs_json_to_the_console()
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        var console = Assert.Single(
            configuration.GetSection("Serilog:WriteTo").GetChildren(),
            sink => sink["Name"] == "Console");

        Assert.StartsWith(
            "Elastic.CommonSchema.Serilog.EcsTextFormatter,",
            console["Args:formatter"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Entity_framework_commands_are_not_logged_at_information()
    {
        // The import worker polls every few seconds; at Information each poll is a log line.
        using var logger = CreateServiceLogger(factory.Services.GetRequiredService<IConfiguration>());

        var efCommands = ForSource(logger, "Microsoft.EntityFrameworkCore.Database.Command");
        Assert.False(efCommands.IsEnabled(LogEventLevel.Information));
        Assert.True(efCommands.IsEnabled(LogEventLevel.Warning));

        Assert.True(
            ForSource(logger, "Cleanse.Api.Imports.ImportProcessorService").IsEnabled(LogEventLevel.Information));
    }

    [Fact]
    public void Configuration_cannot_raise_the_level_audit_events_pass_through()
    {
        // Audit events are written at Information and share the root logger with everything else,
        // so a quieter default for the main log must not reach them.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Warning"
            })
            .Build();

        using var logger = CreateServiceLogger(configuration);

        Assert.True(ForSource(logger, "Cleanse.Api.V1.IssueEndpoints").IsEnabled(LogEventLevel.Information));
    }

    private static Logger CreateServiceLogger(IConfiguration configuration)
    {
        var context = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration };
        var loggerConfiguration = new LoggerConfiguration();
        CdpLogging.Configuration(context, loggerConfiguration);
        return loggerConfiguration.CreateLogger();
    }

    private static ILogger ForSource(ILogger logger, string source) =>
        logger.ForContext(Constants.SourceContextPropertyName, source);
}
