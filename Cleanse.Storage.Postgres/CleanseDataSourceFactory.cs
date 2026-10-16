using Amazon;
using Amazon.RDS.Util;
using Npgsql;

namespace Cleanse.Storage.Postgres;

/// <summary>
/// Builds the Npgsql data source. On CDP no database password exists: the service's IAM role is
/// the credential, and a short-lived token is signed locally per refresh.
/// </summary>
public static class CleanseDataSourceFactory
{
    /// <summary>
    /// RDS auth tokens are valid for 15 minutes. Refreshing at 10 leaves headroom for a token to
    /// be handed to a connection just before it would otherwise expire mid-handshake.
    /// </summary>
    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan TokenFailureRetryInterval = TimeSpan.FromSeconds(5);

    public static NpgsqlDataSource Create(CleansePostgresOptions options, string? hostOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var host = string.IsNullOrWhiteSpace(hostOverride) ? options.Host : hostOverride;

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            Timeout = options.ConnectionTimeoutSeconds,
            CommandTimeout = options.CommandTimeoutSeconds,
            MaxPoolSize = options.MaxPoolSize,

            // Aurora refuses unencrypted connections: pg_hba.conf rejects them outright. The RDS
            // CA arrives via the TRUSTSTORE_* environment variables CDP injects.
            SslMode = options.UseIamAuthentication ? SslMode.VerifyFull : SslMode.Prefer
        };

        if (!options.UseIamAuthentication)
        {
            connectionString.Password = options.LocalPassword;
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);

        if (options.UseIamAuthentication)
        {
            builder.UsePeriodicPasswordProvider(
                (_, _) => ValueTask.FromResult(GenerateAuthToken(options, host)),
                TokenRefreshInterval,
                TokenFailureRetryInterval);
        }

        return builder.Build();
    }

    /// <summary>
    /// Signed locally with SigV4 using the container's role credentials. No network call, so
    /// unlike most AWS SDK work on CDP this needs no Squid proxy allow-listing.
    /// </summary>
    private static string GenerateAuthToken(CleansePostgresOptions options, string host) =>
        RDSAuthTokenGenerator.GenerateAuthToken(
            RegionEndpoint.GetBySystemName(options.Region),
            host,
            options.Port,
            options.Username);
}
