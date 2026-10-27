using Cleanse.Core.Abstractions;
using Cleanse.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Core.Test.Storage;

public sealed class InMemoryUserStoreTests : UserStoreContractTests, IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddCleanseInMemoryStorage().BuildServiceProvider();

    protected override IUserStore CreateStore() => _services.GetRequiredService<IUserStore>();
    protected override IIssueStore CreateIssueStore() => _services.GetRequiredService<IIssueStore>();
    protected override IDataResetStore CreateResetStore() => _services.GetRequiredService<IDataResetStore>();
    public void Dispose() => _services.Dispose();
}
