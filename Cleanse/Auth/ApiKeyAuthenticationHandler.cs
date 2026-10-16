using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cleanse.Auth;

public sealed class ApiKeySchemeOptions : AuthenticationSchemeOptions
{
    public string? Key { get; set; }

    public string ClientName { get; set; } = "api-key-client";
}

/// <summary>
/// Static shared-secret authentication. Proves only that the caller knows the secret, which is
/// why the STS scheme exists alongside it.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeySchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeySchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyOptions.HeaderName, out var provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expected = Options.Key;
        if (string.IsNullOrEmpty(expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("No API key is configured."));
        }

        if (!FixedTimeEquals(provided.ToString(), expected))
        {
            // Never log or echo the supplied value; it may be a near-miss of the real key.
            Logger.LogWarning("Rejected a request carrying an invalid API key.");
            return Task.FromResult(AuthenticateResult.Fail("The supplied API key is not valid."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Options.ClientName),
            new Claim(ClaimTypes.Name, Options.ClientName),
            new Claim(CleanseClaims.CallerKind, CleanseClaims.ApiKeyCaller)
        ], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>
    /// Compared in constant time. A plain string comparison leaks the length of the matching
    /// prefix through timing, which is enough to recover a key given sufficient attempts.
    /// </summary>
    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
}

public static class CleanseClaims
{
    public const string CallerKind = "cleanse:caller_kind";
    public const string ServiceName = "cleanse:service_name";

    public const string ApiKeyCaller = "api-key";
    public const string ServiceCaller = "service";
}
