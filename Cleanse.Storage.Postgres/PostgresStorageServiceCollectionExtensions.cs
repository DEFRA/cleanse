using Cleanse.Core.Abstractions;
using Cleanse.Storage.Postgres.Schema;
using Cleanse.Storage.Postgres.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Cleanse.Storage.Postgres;

public static class PostgresStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Aurora PostgreSQL provider. No schema is created or verified at startup:
    /// the service connects as the DML user, which has no DDL rights by design. Migrations are
    /// applied out of band through the CDP Portal.
    /// </summary>
    public static IServiceCollection AddCleansePostgresStorage(
        this IServiceCollection services,
        Action<CleansePostgresOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CleansePostgresOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(_ => CleanseDataSourceFactory.Create(options));

        services.AddDbContext<CleanseDbContext>((provider, builder) =>
            builder.UseNpgsql(
                provider.GetRequiredService<NpgsqlDataSource>(),
                npgsql => npgsql.CommandTimeout(options.CommandTimeoutSeconds)));

        return AddStores(services);
    }

    /// <summary>
    /// Registers the provider against a data source the caller already owns. Used by the
    /// integration tests, which point at a throwaway container rather than Aurora.
    /// </summary>
    public static IServiceCollection AddCleansePostgresStorage(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        CleansePostgresOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.TryAddSingleton(options ?? CleansePostgresOptions.ForExistingDataSource());
        services.AddDbContext<CleanseDbContext>(builder => builder.UseNpgsql(dataSource));

        return AddStores(services);
    }

    private static IServiceCollection AddStores(IServiceCollection services)
    {
        services.TryAddScoped<IIssueStore, PostgresIssueStore>();
        services.TryAddScoped<IUserStore, PostgresUserStore>();
        services.TryAddScoped<IAuditStore, PostgresAuditStore>();
        services.TryAddScoped<IIssueHistoryStore, PostgresIssueHistoryStore>();
        services.TryAddScoped<IRuleRunStore, PostgresRuleRunStore>();
        services.TryAddScoped<IImportJobStore, PostgresImportJobStore>();
        services.TryAddScoped<IDataResetStore, PostgresDataResetStore>();
        services.TryAddScoped<IStorageDiagnostics, PostgresStorageDiagnostics>();
        return services;
    }
}
