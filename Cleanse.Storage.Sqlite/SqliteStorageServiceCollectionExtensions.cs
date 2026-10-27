using Cleanse.Core.Abstractions;
using Cleanse.Storage.Sqlite.Schema;
using Cleanse.Storage.Sqlite.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cleanse.Storage.Sqlite;

public static class SqliteStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite provider. Unlike the Aurora provider this one may create its own
    /// schema, so call <see cref="CleanseSqliteDatabase.InitialiseAsync"/> once at startup.
    /// </summary>
    public static IServiceCollection AddCleanseSqliteStorage(
        this IServiceCollection services,
        Action<CleanseSqliteOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CleanseSqliteOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(provider =>
            new CleanseSqliteDatabase(provider.GetRequiredService<CleanseSqliteOptions>()));

        services.TryAddScoped<IIssueStore, SqliteIssueStore>();
        services.TryAddScoped<IUserStore, SqliteUserStore>();
        services.TryAddScoped<IAuditStore, SqliteAuditStore>();
        services.TryAddScoped<IIssueHistoryStore, SqliteIssueHistoryStore>();
        services.TryAddScoped<IRuleRunStore, SqliteRuleRunStore>();
        services.TryAddScoped<IImportJobStore, SqliteImportJobStore>();
        services.TryAddScoped<IDataResetStore, SqliteDataResetStore>();
        services.TryAddScoped<IStorageDiagnostics, SqliteStorageDiagnostics>();

        return services;
    }
}
