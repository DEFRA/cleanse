namespace Cleanse.Storage.Postgres;

/// <summary>
/// Connection settings. Mirrors the environment variable names CDP already uses for Postgres
/// tenants, so <c>cdp-app-config</c> entries can be copied across services unchanged.
/// </summary>
public sealed class CleansePostgresOptions
{
    /// <summary>Writer endpoint, e.g. <c>cleanse.cluster-xxxx.eu-west-2.rds.amazonaws.com</c>.</summary>
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    /// <summary>CDP derives this from the service name with underscores.</summary>
    public string Database { get; set; } = "cleanse";

    /// <summary>The DML user. The DDL user is never used by the running service.</summary>
    public string Username { get; set; } = "cleanse";

    /// <summary>
    /// Reader endpoint. Configured so it is ready, but deliberately unused: the detection path
    /// reads an issue immediately before writing it, and replica lag would create duplicates.
    /// </summary>
    public string? ReadOnlyHost { get; set; }

    /// <summary>On CDP there is no password; a short-lived IAM token is minted per connection.</summary>
    public bool UseIamAuthentication { get; set; } = true;

    /// <summary>Local development only. Ignored when <see cref="UseIamAuthentication"/> is set.</summary>
    public string? LocalPassword { get; set; }

    /// <summary>
    /// Set when the caller supplies an already-built <c>NpgsqlDataSource</c>: the connection
    /// string is fixed by then, so no credential is ever read from these options.
    /// </summary>
    public bool CredentialsSuppliedByDataSource { get; set; }

    public string Region { get; set; } = "eu-west-2";

    /// <summary>
    /// Aurora Serverless v2 takes 2-9 seconds to answer the first query after idling, so the
    /// default 15 second Npgsql timeout is too tight for a cold cluster.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    public int CommandTimeoutSeconds { get; set; } = 60;

    public int MaxPoolSize { get; set; } = 20;

    /// <summary>Rows above which the batch path switches from multi-row INSERT to binary COPY.</summary>
    public int CopyThreshold { get; set; } = 100;

    /// <summary>
    /// Options for a data source the caller has already built, such as a test container or a
    /// connection configured elsewhere. Exists so those callers need no placeholder password:
    /// one only ever existed to satisfy validation, and a literal assigned to a property named
    /// Password reads as a leaked credential to secret scanners.
    /// </summary>
    public static CleansePostgresOptions ForExistingDataSource(int? copyThreshold = null)
    {
        var options = new CleansePostgresOptions
        {
            UseIamAuthentication = false,
            CredentialsSuppliedByDataSource = true
        };

        if (copyThreshold is { } threshold)
        {
            options.CopyThreshold = threshold;
        }

        return options;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host, nameof(Host));
        ArgumentException.ThrowIfNullOrWhiteSpace(Database, nameof(Database));
        ArgumentException.ThrowIfNullOrWhiteSpace(Username, nameof(Username));
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1, nameof(Port));
        ArgumentOutOfRangeException.ThrowIfLessThan(CopyThreshold, 1, nameof(CopyThreshold));

        if (!UseIamAuthentication && !CredentialsSuppliedByDataSource &&
            string.IsNullOrEmpty(LocalPassword))
        {
            throw new InvalidOperationException(
                $"{nameof(LocalPassword)} is required when {nameof(UseIamAuthentication)} is disabled.");
        }
    }
}
