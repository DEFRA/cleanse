using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Test.Storage;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.Sqlite.Schema;
using Cleanse.Storage.Sqlite.Stores;

namespace Cleanse.Storage.Sqlite.Test;

/// <summary>
/// A private in-memory database per test class. No Docker, no daemon, no image pull, so these
/// run everywhere the solution builds.
/// </summary>
internal sealed class SqliteHarness : IAsyncDisposable
{
    private readonly CleanseSqliteDatabase _database;

    public SqliteHarness()
    {
        Options = new CleanseSqliteOptions { InMemoryName = $"cleanse-{Guid.NewGuid():N}" };
        _database = new CleanseSqliteDatabase(Options);
        _database.InitialiseAsync().GetAwaiter().GetResult();
    }

    public CleanseSqliteOptions Options { get; }

    internal CleanseSqliteDatabase Database => _database;

    public SqliteIssueStore Issues => new(_database, Options);

    public SqliteAuditStore Audit => new(_database);

    public SqliteIssueHistoryStore History => new(_database);

    public SqliteRuleRunStore Runs => new(_database);

    public SqliteDataResetStore Reset => new(_database);

    public SqliteImportJobStore Imports => new(_database);

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqliteIssueStoreTests : IssueStoreContractTests, IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();

    protected override IIssueStore CreateStore() => _harness.Issues;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

public sealed class SqliteAuditStoreTests : AuditStoreContractTests, IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();

    protected override IAuditStore CreateStore() => _harness.Audit;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

public sealed class SqliteIssueHistoryStoreTests : IssueHistoryStoreContractTests, IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();

    protected override IIssueHistoryStore CreateStore() => _harness.History;

    /// <summary>History rows carry a foreign key, so the parent has to exist first.</summary>
    protected override Task EnsureIssueExistsAsync(IssueId issueId) =>
        _harness.Issues.SaveAsync(
            Issue.FromDetection(
                new IssueDetection(issueId, TestData.Rule2), TestData.Group, "run-1", TestData.Night1),
            expectedVersion: null);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

public sealed class SqliteRuleRunStoreTests : RuleRunStoreContractTests, IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();

    protected override IRuleRunStore CreateStore() => _harness.Runs;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

public sealed class SqliteImportJobStoreTests : ImportJobStoreContractTests, IAsyncDisposable
{
    private readonly SqliteHarness _harness = new();

    protected override IImportJobStore CreateStore() => _harness.Imports;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}
