using Cleanse.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cleanse.Api.Test;

public class StorageGuardTests
{
    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Cleanse";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IServiceCollection Services() => new ServiceCollection().AddLogging();

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("InMemory")]
    public void A_non_durable_provider_is_refused_outside_development(string provider)
    {
        // CDP sets ASPNETCORE_ENVIRONMENT=Production for every service in every environment, so
        // this is what stands between a misconfigured deploy and silent data loss.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddCleanseStorage(
                new StorageOptions { Provider = provider },
                new StubEnvironment(Environments.Production)));

        Assert.Contains("Only Postgres", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("InMemory")]
    public void A_non_durable_provider_is_allowed_in_development(string provider)
    {
        var services = Services().AddCleanseStorage(
            new StorageOptions { Provider = provider },
            new StubEnvironment(Environments.Development));

        Assert.NotNull(services);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MySql")]
    public void An_unrecognised_provider_is_refused(string? provider)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Services().AddCleanseStorage(
                new StorageOptions { Provider = provider },
                new StubEnvironment(Environments.Development)));

        Assert.Contains("Storage:Provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_is_accepted_in_production()
    {
        var services = Services().AddCleanseStorage(
            new StorageOptions { Provider = "Postgres" },
            new StubEnvironment(Environments.Production));

        Assert.NotNull(services);
    }
}
