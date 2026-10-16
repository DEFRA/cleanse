using Cleanse.Core.Abstractions;
using Cleanse.Core.DependencyInjection;
using Cleanse.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Core.Test.Support;

/// <summary>
/// Hosts Core in a real container with the in-memory provider plugged in. This is the whole
/// point of the exercise: Core is exercised exactly as an ASP.NET host would exercise it, with
/// nothing mocked, so the only thing swapped out when Postgres arrives is the storage package.
/// </summary>
internal sealed class CleanseHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    public CleanseHarness(DateTimeOffset? startAt = null, Action<CleanseCoreOptions>? configure = null)
    {
        Clock = new TestClock(startAt ?? TestData.Night1);

        var services = new ServiceCollection();

        // Registered before AddCleanseCore so its TryAddSingleton(TimeProvider.System) backs off.
        services.AddSingleton<TimeProvider>(Clock);
        services.AddCleanseCore(configure);
        services.AddCleanseInMemoryStorage();

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    public TestClock Clock { get; }

    public ICleanseDispatcher Dispatcher => _provider.GetRequiredService<ICleanseDispatcher>();

    public InMemoryIssueStore Issues => _provider.GetRequiredService<InMemoryIssueStore>();

    public InMemoryAuditStore Audit => _provider.GetRequiredService<InMemoryAuditStore>();

    public InMemoryIssueHistoryStore History => _provider.GetRequiredService<InMemoryIssueHistoryStore>();

    public InMemoryRuleRunStore Runs => _provider.GetRequiredService<InMemoryRuleRunStore>();

    /// <summary>Dispatches and asserts success, returning the value.</summary>
    public async Task<T> SendAsync<T>(ICommand<T> command, CancellationToken cancellationToken = default)
    {
        var result = await Dispatcher.SendAsync(command, cancellationToken);
        Assert.True(result.IsSuccess, $"Expected success but got {result.Error}");
        return result.Value;
    }

    /// <summary>Dispatches and returns the raw result, for asserting on failures.</summary>
    public Task<Result<T>> TrySendAsync<T>(ICommand<T> command, CancellationToken cancellationToken = default) =>
        Dispatcher.SendAsync(command, cancellationToken);

    public async Task<T> QueryAsync<T>(IQuery<T> query, CancellationToken cancellationToken = default)
    {
        var result = await Dispatcher.QueryAsync(query, cancellationToken);
        Assert.True(result.IsSuccess, $"Expected success but got {result.Error}");
        return result.Value;
    }

    public Task<Result<T>> TryQueryAsync<T>(IQuery<T> query, CancellationToken cancellationToken = default) =>
        Dispatcher.QueryAsync(query, cancellationToken);

    public void Dispose() => _provider.Dispose();
}
