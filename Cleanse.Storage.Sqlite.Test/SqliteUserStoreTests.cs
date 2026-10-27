using Cleanse.Core.Abstractions;
using Cleanse.Core.Test.Storage;
using Cleanse.Storage.Sqlite.Stores;

namespace Cleanse.Storage.Sqlite.Test;

public sealed class SqliteUserStoreTests : UserStoreContractTests, IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();
    protected override IUserStore CreateStore() => new SqliteUserStore(_harness.Database);
    protected override IIssueStore CreateIssueStore() => _harness.Issues;
    protected override IDataResetStore CreateResetStore() => _harness.Reset;
    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
