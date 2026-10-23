using System.Runtime.InteropServices;
using Cleanse.Core.Contracts;

namespace Cleanse.Api.Diagnostics;

/// <summary>
/// What this process is, captured once at startup so a status page can say which build answered.
/// </summary>
public sealed record ServiceInfo(
    string Name,
    string? Version,
    string? Environment,
    string Runtime,
    DateTimeOffset StartedAtUtc)
{
    public const string ServiceName = "cleanse";

    /// <summary>
    /// Reads SERVICE_VERSION and ENVIRONMENT, which CDP injects into every container. Both are
    /// absent locally, and are reported as null rather than guessed.
    /// </summary>
    /// <remarks>
    /// Read from the process environment rather than IConfiguration: ASP.NET Core strips the
    /// ASPNETCORE_ prefix when it loads environment variables, so the configuration key
    /// ENVIRONMENT also carries ASPNETCORE_ENVIRONMENT, which is Production everywhere on CDP.
    /// </remarks>
    public static ServiceInfo FromEnvironment(TimeProvider clock) =>
        FromEnvironment(System.Environment.GetEnvironmentVariable, clock);

    internal static ServiceInfo FromEnvironment(Func<string, string?> variable, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(clock);

        return new ServiceInfo(
            ServiceName,
            Blank(variable("SERVICE_VERSION")),
            Blank(variable("ENVIRONMENT")),
            RuntimeInformation.FrameworkDescription,
            clock.GetUtcNow());
    }

    public ServiceInfoDto ToDto() => new(Name, Version, Environment, Runtime, StartedAtUtc);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
