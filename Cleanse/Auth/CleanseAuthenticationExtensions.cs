using System.Security.Claims;
using System.Text.Json;
using Cleanse.Utils.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cleanse.Auth;

public static class CleanseAuthenticationExtensions
{
    public const string DefaultScheme = "Cleanse";

    /// <summary>Used only when no credential scheme is configured, so the API is closed by default.</summary>
    public const string DenyAllScheme = "DenyAll";

    /// <summary>
    /// Registers whichever schemes are configured and forwards each request to the right one.
    /// Both land on the same authorisation policy; the STS allow-list is applied on top.
    /// </summary>
    public static IServiceCollection AddCleanseAuthentication(
        this IServiceCollection services,
        CleanseAuthOptions options,
        IHostEnvironment environment,
        StartupWarnings warnings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(warnings);

        var apiKeyEnabled = options.ApiKey.IsConfigured;
        var jwtEnabled = options.Jwt.Enabled;

        if (!apiKeyEnabled && !jwtEnabled && !environment.IsDevelopment())
        {
            // Deliberately not a throw. Refusing every request is safe and diagnosable, whereas
            // crashing would also break build-time OpenAPI generation, which legitimately has no
            // deployment secrets.
            warnings.Add(
                "Neither Auth:ApiKey:Key nor Auth:Jwt:Enabled is configured. Every request to " +
                "/v1 will be rejected with 401.");
        }

        // Injected directly: the reset endpoint is the only thing that reads it, and it needs a
        // plain value rather than an options snapshot.
        services.AddSingleton(options.Reset);

        // Validated on host start. Only covers config that is unambiguously broken rather than
        // merely absent, so a build never trips it.
        services.AddOptions<CleanseAuthOptions>()
            .Configure(target =>
            {
                target.ApiKey = options.ApiKey;
                target.Jwt = options.Jwt;
                target.Reset = options.Reset;
            })
            .Validate(
                target => !target.Jwt.Enabled ||
                          !string.IsNullOrWhiteSpace(target.Jwt.MetadataAddress) ||
                          !string.IsNullOrWhiteSpace(target.Jwt.Issuer),
                "Auth:Jwt is enabled but neither MetadataAddress (CDP_JWT_OIDC_URL) nor Issuer " +
                "(CDP_JWT_ISSUER) is set.")
            .ValidateOnStart();

        var builder = services.AddAuthentication(DefaultScheme);

        builder.AddPolicyScheme(DefaultScheme, DefaultScheme, scheme =>
            scheme.ForwardDefaultSelector = context =>
            {
                if (apiKeyEnabled && context.Request.Headers.ContainsKey(ApiKeyOptions.HeaderName))
                {
                    return ApiKeyAuthenticationHandler.SchemeName;
                }

                if (jwtEnabled)
                {
                    return JwtBearerDefaults.AuthenticationScheme;
                }

                // Falls through to whichever scheme exists, so the caller gets a 401 rather than
                // an internal error when they present the wrong kind of credential.
                return apiKeyEnabled ? ApiKeyAuthenticationHandler.SchemeName : DenyAllScheme;
            });

        if (!apiKeyEnabled && !jwtEnabled)
        {
            builder.AddScheme<AuthenticationSchemeOptions, DenyAllAuthenticationHandler>(
                DenyAllScheme, _ => { });
        }

        if (apiKeyEnabled)
        {
            builder.AddScheme<ApiKeySchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                scheme =>
                {
                    scheme.Key = options.ApiKey.Key;
                    scheme.ClientName = options.ApiKey.ClientName;
                });
        }

        if (jwtEnabled)
        {
            AddCdpJwtBearer(builder, options.Jwt);
        }

        return services;
    }

    private static void AddCdpJwtBearer(AuthenticationBuilder builder, JwtOptions jwt)
    {
        builder.AddJwtBearer(options =>
        {
            if (!string.IsNullOrWhiteSpace(jwt.MetadataAddress))
            {
                options.MetadataAddress = jwt.MetadataAddress;
            }

            options.Authority = jwt.Issuer;
            options.RequireHttpsMetadata = true;

            // The STS issuer is an external AWS URL and all egress is blocked except via Squid.
            // Without this the JWKS fetch hangs and authentication fails at first use.
            options.BackchannelHttpHandler = new ProxyHttpMessageHandler(
                LoggerFactory.Create(logging => logging.AddConsole())
                    .CreateLogger<ProxyHttpMessageHandler>());

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = !string.IsNullOrWhiteSpace(jwt.Issuer),
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),

                // Pinned rather than trusting whatever the token header claims.
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha384]
            };

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    FlattenStsClaims(context.Principal);
                    return Task.CompletedTask;
                }
            };
        });
    }

    /// <summary>
    /// Lifts principal_tags.ServiceName out of the nested STS claim onto a flat claim, so the
    /// authorisation policy does not have to parse JSON on every request.
    /// </summary>
    private static void FlattenStsClaims(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        identity.AddClaim(new Claim(CleanseClaims.CallerKind, CleanseClaims.ServiceCaller));

        var serviceName = ReadServiceName(identity);
        if (serviceName is not null)
        {
            identity.AddClaim(new Claim(CleanseClaims.ServiceName, serviceName));
        }
    }

    private static string? ReadServiceName(ClaimsIdentity identity)
    {
        const string stsClaim = "https://sts.amazonaws.com/";

        var raw = identity.FindFirst(stsClaim)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("principal_tags", out var tags) ||
                !tags.TryGetProperty("ServiceName", out var name))
            {
                return null;
            }

            return name.ValueKind switch
            {
                JsonValueKind.String => name.GetString(),
                // Session tags can arrive as single-element arrays.
                JsonValueKind.Array when name.GetArrayLength() > 0 => name[0].GetString(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? GetServiceName(this JsonWebToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.TryGetPayloadValue<JsonElement>("https://sts.amazonaws.com/", out var sts) &&
               sts.TryGetProperty("principal_tags", out var tags) &&
               tags.TryGetProperty("ServiceName", out var name)
            ? name.GetString()
            : null;
    }
}
