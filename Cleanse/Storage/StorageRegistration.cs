using Cleanse.Storage.InMemory;
using Cleanse.Storage.Postgres;
using Cleanse.Storage.Sqlite;
using Cleanse.Storage.Sqlite.Schema;

namespace Cleanse.Storage;

public enum StorageProvider
{
    InMemory,
    Sqlite,
    Postgres
}

/// <summary>Which provider startup resolved, so later stages can branch without re-reading config.</summary>
public sealed record SelectedStorageProvider(StorageProvider Provider);

public sealed class StorageOptions
{
    /// <summary>No default: an unset or unrecognised value is a configuration error, not a hint.</summary>
    public string? Provider { get; set; }

    public CleansePostgresOptions Postgres { get; set; } = new();

    public CleanseSqliteOptions Sqlite { get; set; } = new();
}

public static class StorageRegistration
{
    /// <summary>
    /// Selects one provider from configuration. CDP sets ASPNETCORE_ENVIRONMENT=Production for
    /// every service in every environment, so anything other than Development must be Postgres:
    /// a deployed service on SQLite would look healthy and lose all its data on the next recycle.
    /// </summary>
    public static IServiceCollection AddCleanseStorage(
        this IServiceCollection services,
        StorageOptions options,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (!Enum.TryParse<StorageProvider>(options.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Storage:Provider must be one of {string.Join(", ", Enum.GetNames<StorageProvider>())}; " +
                $"found '{options.Provider ?? "<unset>"}'.");
        }

        if (!environment.IsDevelopment() && provider != StorageProvider.Postgres)
        {
            throw new InvalidOperationException(
                $"Storage:Provider is '{provider}' but the hosting environment is " +
                $"'{environment.EnvironmentName}'. Only Postgres durably stores data outside " +
                "Development; refusing to start rather than silently losing issues.");
        }

        switch (provider)
        {
            case StorageProvider.Postgres:
                services.AddCleansePostgresStorage(postgres => Copy(options.Postgres, postgres));
                break;

            case StorageProvider.Sqlite:
                services.AddCleanseSqliteStorage(sqlite =>
                {
                    sqlite.DataSource = options.Sqlite.DataSource;
                    sqlite.InMemoryName = options.Sqlite.InMemoryName;
                    sqlite.BatchSize = options.Sqlite.BatchSize;
                });
                break;

            default:
                services.AddCleanseInMemoryStorage();
                break;
        }

        services.AddSingleton(new SelectedStorageProvider(provider));
        return services;
    }

    /// <summary>Creates the SQLite schema. A no-op for the other providers.</summary>
    public static async Task InitialiseStorageAsync(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetRequiredService<SelectedStorageProvider>().Provider != StorageProvider.Sqlite)
        {
            return;
        }

        await services.GetRequiredService<CleanseSqliteDatabase>().InitialiseAsync();
    }

    private static void Copy(CleansePostgresOptions from, CleansePostgresOptions to)
    {
        to.Host = from.Host;
        to.Port = from.Port;
        to.Database = from.Database;
        to.Username = from.Username;
        to.ReadOnlyHost = from.ReadOnlyHost;
        to.UseIamAuthentication = from.UseIamAuthentication;
        to.LocalPassword = from.LocalPassword;
        to.Region = from.Region;
        to.ConnectionTimeoutSeconds = from.ConnectionTimeoutSeconds;
        to.CommandTimeoutSeconds = from.CommandTimeoutSeconds;
        to.MaxPoolSize = from.MaxPoolSize;
        to.CopyThreshold = from.CopyThreshold;
    }
}
