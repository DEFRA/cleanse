using Cleanse.Core.Abstractions;
using Cleanse.Core.Test.Storage;
using Cleanse.Storage.Postgres.Stores;
using Cleanse.Storage.Postgres.Test.Support;

namespace Cleanse.Storage.Postgres.Test;

[Collection(PostgresCollection.Name)]
public sealed class PostgresUserStoreTests(PostgresFixture fixture) : UserStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);
    protected override IUserStore CreateStore() => new PostgresUserStore(_harness.NewContext());
    protected override IIssueStore CreateIssueStore() =>
        new PostgresIssueStore(_harness.NewContext(), CleansePostgresOptions.ForExistingDataSource(3));
    protected override IDataResetStore CreateResetStore() => new PostgresDataResetStore(_harness.NewContext());
    public ValueTask InitializeAsync() => _harness.InitializeAsync();
    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
