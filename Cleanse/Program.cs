using System.Diagnostics.CodeAnalysis;
using Cleanse;
using Cleanse.Api;
using Cleanse.Api.Diagnostics;
using Cleanse.Api.Imports;
using Cleanse.Api.V1;
using Cleanse.Auth;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.DependencyInjection;
using Cleanse.RulesEngine;
using Cleanse.Storage;
using Cleanse.Utils;
using Cleanse.Utils.Http;
using Cleanse.Utils.Logging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Serilog;

var app = BuildApp(args);
await app.Services.InitialiseStorageAsync();
await app.RunAsync();

[ExcludeFromCodeCoverage]
static WebApplication BuildApp(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    ConfigureHost(builder);
    ConfigureServices(builder);

    var app = builder.Build();

    ConfigureMiddleware(app);
    ConfigureEndpoints(app);

    return app;
}

[ExcludeFromCodeCoverage]
static void ConfigureHost(WebApplicationBuilder builder) =>
    builder.Host.UseSerilog(CdpLogging.Configuration);

static void ConfigureServices(WebApplicationBuilder builder)
{
    var services = builder.Services;
    var configuration = builder.Configuration;

    // Trust material must be loaded before anything creates outbound connections, including the
    // JWKS fetch and the Aurora TLS handshake.
    services.LoadCustomTrustStoreFromEnvironment();

    services.AddProblemDetails();
    services.AddValidation();
    services.AddHttpContextAccessor();

    var api = configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
    api.Validate();
    services.AddSingleton(api);

    var auth = configuration.GetSection("Auth").Get<CleanseAuthOptions>() ?? new CleanseAuthOptions();
    var warnings = new StartupWarnings();
    services.AddSingleton(warnings);
    ApplyCdpJwtDefaults(auth, warnings);
    services.AddCleanseAuthentication(auth, builder.Environment, warnings);
    services.AddCleanseAuthorization(auth, builder.Environment);

    var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
    services.AddCleanseStorage(storage, builder.Environment);
    services.AddCleanseCore();
    services.AddSingleton(ServiceInfo.FromEnvironment(TimeProvider.System));
    services.AddTransient<IQueryHandler<GetDiagnosticsQuery, DiagnosticsDto>, GetDiagnosticsQueryHandler>();

    var imports = configuration.GetSection("Imports").Get<ImportOptions>() ?? new ImportOptions();
    imports.Validate();
    services.AddSingleton(imports);
    services.AddCleanseImports(imports, builder.Environment, warnings);

    var rules = configuration.GetSection("Rules").Get<RulesSectionOptions>() ?? new RulesSectionOptions();
    services.AddCleanseRulesEngine(rules, warnings);

    ConfigureHeaderPropagation(services, configuration);
    ConfigureHttpClients(services);

    services.AddRequestTimeouts(options =>
        options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromSeconds(api.RequestTimeoutSeconds),
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
        });

    services.AddCleanseOpenApi();

    // Liveness only. A cold Aurora takes 2-9 seconds to answer, and the platform healthcheck
    // kills the container after three 5 second timeouts.
    services.AddHealthChecks();
}

/// <summary>Falls back to the variables CDP injects into every service.</summary>
static void ApplyCdpJwtDefaults(CleanseAuthOptions auth, StartupWarnings warnings)
{
    var issuer = Environment.GetEnvironmentVariable("CDP_JWT_ISSUER");
    auth.Jwt.Issuer ??= issuer;
    auth.Jwt.MetadataAddress ??= Environment.GetEnvironmentVariable("CDP_JWT_OIDC_URL");

    // The platform only injects these on a real deployment, so their presence alongside a
    // disabled scheme almost always means Auth__Jwt__Enabled was missed in cdp-app-config.
    if (!auth.Jwt.Enabled && !string.IsNullOrWhiteSpace(issuer))
    {
        warnings.Add(
            "CDP_JWT_ISSUER is set but Auth:Jwt:Enabled is false. Service-to-service " +
            "authentication is disabled; only the shared API key will be accepted.");
    }
}

[ExcludeFromCodeCoverage]
static void ConfigureHeaderPropagation(IServiceCollection services, IConfiguration configuration)
{
    var traceHeader = configuration.GetValue<string>("TraceHeader");

    services.AddHeaderPropagation(options =>
    {
        if (!string.IsNullOrWhiteSpace(traceHeader))
        {
            options.Headers.Add(traceHeader);
        }
    });
}

[ExcludeFromCodeCoverage]
static void ConfigureHttpClients(IServiceCollection services) =>
    services.AddTransient<ProxyHttpMessageHandler>();

static void ConfigureMiddleware(WebApplication app)
{
    foreach (var warning in app.Services.GetRequiredService<StartupWarnings>().Messages)
    {
        app.Logger.LogWarning("{StartupWarning}", warning);
    }

    app.UseSerilogRequestLogging();
    app.UseHeaderPropagation();
    app.UseRequestTimeouts();
    app.UseAuthentication();
    app.UseAuthorization();
}

static void ConfigureEndpoints(WebApplication app)
{
    app.MapHealthChecks("/health", new HealthCheckOptions()).AllowAnonymous();

    app.MapOpenApi("/v1/openapi.json").AllowAnonymous();

    var v1 = app.MapGroup("/v1").RequireAuthorization(CleansePolicies.Default);

    v1.MapIssueEndpoints();
    v1.MapUserEndpoints();
    v1.MapRunEndpoints();
    v1.MapRuleEndpoints();
    v1.MapGroupEndpoints();
    v1.MapImportEndpoints();
    v1.MapMaintenanceEndpoints();
    v1.MapDiagnosticsEndpoints();
}

/// <summary>Exposed so the API tests can host the real application.</summary>
[ExcludeFromCodeCoverage]
public partial class Program;
