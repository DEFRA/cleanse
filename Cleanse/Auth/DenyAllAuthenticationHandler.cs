using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cleanse.Auth;

/// <summary>
/// Registered only when no credential scheme is configured. Turns a missing-configuration
/// mistake into a uniform 401 rather than an unauthenticated API or a startup crash.
/// </summary>
public sealed class DenyAllAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.Fail(
            "No authentication scheme is configured for this service."));
}
