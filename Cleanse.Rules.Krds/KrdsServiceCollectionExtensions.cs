using System.Net.Http.Headers;
using Cleanse.Core.Abstractions;
using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Krds.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Krds;

public static class KrdsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the KRDS snapshot data source.
    /// </summary>
    /// <returns>
    /// Null when the source was configured, or the reason it was not. Misconfiguration is
    /// reported rather than thrown so that a host without deployment configuration still starts;
    /// a fail-closed stub is registered and only a run that needs the snapshot fails.
    /// </returns>
    public static string? AddKrdsDataSource(this IServiceCollection services, KrdsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ICtsSamDataSourceFactory, KrdsCtsSamDataSourceFactory>();

        // The bridge's health check is anonymous, so the probe can ask it as soon as there is a
        // bridge to ask, even while the key or the rest of the configuration is still missing.
        if (options.ResolveProvider() is KrdsSnapshotProvider.DataBridge)
        {
            services.AddHttpClient(KrdsDiagnostics.HealthClientName, client =>
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")));
        }

        // Registered whatever the outcome below: a status page is most useful when the thing it
        // reports on is misconfigured, so the probe must exist even when the source does not.
        services.TryAddSingleton<IKrdsDiagnostics>(provider => new KrdsDiagnostics(
            options, provider.GetService<IHttpClientFactory>()));

        var problem = options.Describe();
        if (problem is not null)
        {
            services.TryAddSingleton<IKrdsSnapshotSource>(_ => new UnconfiguredSnapshotSource(problem));
            return problem;
        }

        if (options.ResolveProvider() is KrdsSnapshotProvider.LocalFile)
        {
            services.TryAddSingleton<IKrdsSnapshotSource>(_ =>
                new LocalFileSnapshotSource(options.LocalFilePath!));
            return null;
        }

        services.AddHttpClient(DataBridgeSnapshotSource.ApiClientName, client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // Separate client: the snapshot runs to the better part of a gigabyte and needs a far
        // longer timeout than an API call should ever be given.
        services.AddHttpClient(DataBridgeSnapshotSource.DownloadClientName, client =>
            client.Timeout = options.DownloadTimeout);

        services.TryAddSingleton<IKrdsSnapshotSource>(provider => new DataBridgeSnapshotSource(
            provider.GetRequiredService<IHttpClientFactory>(),
            options,
            provider.GetRequiredService<ILogger<DataBridgeSnapshotSource>>()));

        return null;
    }
}
