using Amazon.S3;
using Cleanse.Core.Abstractions;
using Cleanse.Storage.Sqlite.Stores;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cleanse.Api.Imports;

public static class ImportRegistration
{
    /// <summary>
    /// Picks a file store the same way storage picks a provider: S3 on CDP, the local filesystem
    /// in development. A missing bucket is not thrown at registration, because build-time OpenAPI
    /// generation constructs the host and a document should not require deployment config.
    /// </summary>
    public static IServiceCollection AddCleanseImports(
        this IServiceCollection services,
        ImportOptions options,
        IHostEnvironment environment,
        StartupWarnings warnings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(warnings);

        if (environment.IsDevelopment())
        {
            var root = string.IsNullOrWhiteSpace(options.FileStoreRoot)
                ? Path.Combine(Path.GetTempPath(), "cleanse-imports")
                : options.FileStoreRoot;

            services.TryAddSingleton<IImportFileStore>(_ => new FileSystemImportFileStore(root));
            services.TryAddSingleton<IImportStorageDiagnostics>(_ => new FileSystemImportStorageDiagnostics(root));
        }
        else if (string.IsNullOrWhiteSpace(options.S3BucketName))
        {
            // Refusing uploads is safe and diagnosable. Writing them to container-local disk
            // would look like success and lose the file on the next deployment.
            warnings.Add(
                "Imports:S3BucketName is not configured. Uploads will be rejected until it is set.");

            services.TryAddSingleton<IImportFileStore>(_ => new UnconfiguredImportFileStore());
            services.TryAddSingleton<IImportStorageDiagnostics>(_ => new UnconfiguredImportStorageDiagnostics());
        }
        else
        {
            services.TryAddSingleton<IAmazonS3>(_ => new AmazonS3Client());
            services.TryAddSingleton<IImportFileStore>(provider =>
                new S3ImportFileStore(
                    provider.GetRequiredService<IAmazonS3>(), options.S3BucketName));
            services.TryAddSingleton<IImportStorageDiagnostics>(provider =>
                new S3ImportStorageDiagnostics(
                    provider.GetRequiredService<IAmazonS3>(), options.S3BucketName));
        }

        services.AddHostedService<ImportProcessorService>();
        return services;
    }
}

/// <summary>Stands in when no bucket is configured, so the failure is explicit rather than silent.</summary>
internal sealed class UnconfiguredImportFileStore : IImportFileStore
{
    private const string Message =
        "No import file store is configured. Set Imports:S3BucketName for this environment.";

    public Task WriteAsync(string key, Stream content, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
