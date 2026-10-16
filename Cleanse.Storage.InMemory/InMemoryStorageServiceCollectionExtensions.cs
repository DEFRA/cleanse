using Cleanse.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cleanse.Storage.InMemory;

public static class InMemoryStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory storage provider. Singletons, because the data lives in the
    /// instance. Intended for tests and local spikes; state is lost when the process ends.
    /// </summary>
    public static IServiceCollection AddCleanseInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryIssueStore>();
        services.TryAddSingleton<InMemoryAuditStore>();
        services.TryAddSingleton<InMemoryIssueHistoryStore>();
        services.TryAddSingleton<InMemoryRuleRunStore>();
        services.TryAddSingleton<InMemoryImportJobStore>();
        services.TryAddSingleton<InMemoryImportFileStore>();
        services.TryAddSingleton<InMemoryDataResetStore>();

        services.TryAddSingleton<IIssueStore>(sp => sp.GetRequiredService<InMemoryIssueStore>());
        services.TryAddSingleton<IAuditStore>(sp => sp.GetRequiredService<InMemoryAuditStore>());
        services.TryAddSingleton<IIssueHistoryStore>(sp => sp.GetRequiredService<InMemoryIssueHistoryStore>());
        services.TryAddSingleton<IRuleRunStore>(sp => sp.GetRequiredService<InMemoryRuleRunStore>());
        services.TryAddSingleton<IImportJobStore>(sp => sp.GetRequiredService<InMemoryImportJobStore>());
        services.TryAddSingleton<IImportFileStore>(sp => sp.GetRequiredService<InMemoryImportFileStore>());
        services.TryAddSingleton<IDataResetStore>(sp => sp.GetRequiredService<InMemoryDataResetStore>());
        services.TryAddSingleton<IStorageDiagnostics, InMemoryStorageDiagnostics>();

        return services;
    }
}
