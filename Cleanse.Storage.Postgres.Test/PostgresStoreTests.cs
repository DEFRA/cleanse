using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Storage;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.Postgres.Schema;
using Cleanse.Storage.Postgres.Stores;
using Cleanse.Storage.Postgres.Test.Support;

namespace Cleanse.Storage.Postgres.Test;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIssueStoreTests(PostgresFixture fixture)
    : IssueStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IIssueStore CreateStore() =>
        // Threshold low enough that the batch tests exercise COPY rather than INSERT.
        new PostgresIssueStore(_harness.NewContext(), CleansePostgresOptions.ForExistingDataSource(3));

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresAuditStoreTests(PostgresFixture fixture)
    : AuditStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IAuditStore CreateStore() => new PostgresAuditStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresIssueHistoryStoreTests(PostgresFixture fixture)
    : IssueHistoryStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IIssueHistoryStore CreateStore() =>
        new PostgresIssueHistoryStore(_harness.NewContext());

    /// <summary>History rows carry a foreign key, so the parent has to exist first.</summary>
    protected override async Task EnsureIssueExistsAsync(IssueId issueId)
    {
        var store = new PostgresIssueStore(
            _harness.NewContext(), CleansePostgresOptions.ForExistingDataSource());

        var detection = new IssueDetection(issueId, TestData.Rule2);
        await store.SaveAsync(
            Issue.FromDetection(detection, TestData.Group, "run-1", TestData.Night1),
            expectedVersion: null);
    }

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresRuleRunStoreTests(PostgresFixture fixture)
    : RuleRunStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IRuleRunStore CreateStore() => new PostgresRuleRunStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresImportJobStoreTests(PostgresFixture fixture)
    : ImportJobStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IImportJobStore CreateStore() => new PostgresImportJobStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

/// <summary>
/// Composition rather than inheritance, because each contract suite is already a base class.
/// </summary>
internal sealed class PostgresStoreHarness(PostgresFixture fixture)
{
    private readonly List<CleanseDbContext> _contexts = [];

    public CleanseDbContext NewContext()
    {
        Assert.SkipWhen(!fixture.IsAvailable, fixture.SkipReason ?? "Docker is not available.");
        var context = fixture.CreateContext();
        _contexts.Add(context);
        return context;
    }

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
    }
}
