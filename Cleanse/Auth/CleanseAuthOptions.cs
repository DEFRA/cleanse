using System.Security.Cryptography;
using System.Text;

namespace Cleanse.Auth;

public sealed class CleanseAuthOptions
{
    public ApiKeyOptions ApiKey { get; set; } = new();

    public JwtOptions Jwt { get; set; } = new();

    public ResetKeyOptions Reset { get; set; } = new();

    public UserDirectoryAuthOptions UserDirectory { get; set; } = new();
}

public sealed class UserDirectoryAuthOptions
{
    /// <summary>
    /// Local Docker only: explicitly trusts the configured machine API key for identity
    /// attestation outside Development. Also requires ApiKey:ClientName to be cleanse-app.
    /// Never enable on a deployed service.
    /// </summary>
    public bool AllowLocalApiKey { get; set; }
}

/// <summary>
/// A second secret, held apart from <see cref="ApiKeyOptions"/>, guarding the one operation
/// that destroys data. A caller holding the API key can read and triage everything, and that is
/// no reason to let it empty the database.
/// </summary>
public sealed class ResetKeyOptions
{
    public const string HeaderName = "x-reset-key";

    /// <summary>Supplied as a Portal secret. Absent means the reset endpoint refuses everyone.</summary>
    public string? Key { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);

    /// <summary>
    /// Constant time, so a near miss cannot leak the length of the matching prefix through
    /// response timing.
    /// </summary>
    public bool Matches(string? supplied) =>
        IsConfigured &&
        !string.IsNullOrWhiteSpace(supplied) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(Key!));
}

public sealed class ApiKeyOptions
{
    /// <summary>
    /// Deliberately not x-api-key: the CDP ephemeral API Gateway already uses that header, and a
    /// collision would mean our key either never arrives or leaks to the gateway.
    /// </summary>
    public const string HeaderName = "x-cleanse-api-key";

    /// <summary>Supplied as a Portal secret. When absent the scheme is not registered at all.</summary>
    public string? Key { get; set; }

    /// <summary>Recorded as the actor on anything a key-authenticated caller changes.</summary>
    public string ClientName { get; set; } = "api-key-client";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);
}

public sealed class JwtOptions
{
    /// <summary>
    /// Off locally: LocalStack and Floci do not implement GetWebIdentityToken, so there is no
    /// token to validate and nothing useful to stub.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Not a platform value. Chosen by us and configured on both caller and receiver.</summary>
    public string Audience { get; set; } = "cleanse";

    /// <summary>From CDP_JWT_ISSUER.</summary>
    public string? Issuer { get; set; }

    /// <summary>From CDP_JWT_OIDC_URL. Fetched through the Squid proxy.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>
    /// CDP service names permitted to call this API with a web identity token. Empty means any
    /// service in the environment can call us, which is the platform default and rarely wanted.
    /// </summary>
    public IList<string> AllowedServiceNames { get; set; } = [];
}
