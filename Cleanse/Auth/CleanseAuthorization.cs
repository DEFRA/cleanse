using Microsoft.AspNetCore.Authorization;

namespace Cleanse.Auth;

public static class CleansePolicies
{
    public const string Default = "cleanse";
}

/// <summary>
/// Authentication proves who is calling; this decides whether they may. By default any service in
/// a CDP environment can obtain a valid web identity token for us, so service callers are held to
/// an allow-list. API key callers hold the shared secret and are not subject to it.
/// </summary>
public sealed class AllowedCallerRequirement(IReadOnlyCollection<string> allowedServiceNames)
    : IAuthorizationRequirement
{
    public IReadOnlyCollection<string> AllowedServiceNames { get; } = allowedServiceNames;
}

public sealed class AllowedCallerHandler(ILogger<AllowedCallerHandler> logger)
    : AuthorizationHandler<AllowedCallerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AllowedCallerRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        var callerKind = context.User.FindFirst(CleanseClaims.CallerKind)?.Value;

        if (callerKind == CleanseClaims.ApiKeyCaller)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (requirement.AllowedServiceNames.Count == 0)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var serviceName = context.User.FindFirst(CleanseClaims.ServiceName)?.Value;
        if (serviceName is not null && requirement.AllowedServiceNames.Contains(serviceName))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        logger.LogWarning(
            "Rejected a token from service {ServiceName}, which is not in the allow list.",
            serviceName ?? "<unknown>");

        return Task.CompletedTask;
    }
}

public static class CleanseAuthorizationExtensions
{
    public static IServiceCollection AddCleanseAuthorization(
        this IServiceCollection services,
        CleanseAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IAuthorizationHandler, AllowedCallerHandler>();

        var allowed = options.Jwt.AllowedServiceNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        services.AddAuthorizationBuilder()
            .AddPolicy(CleansePolicies.Default, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new AllowedCallerRequirement(allowed));
            })
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new AllowedCallerRequirement(allowed))
                .Build());

        return services;
    }
}
