using Cleanse.Core.Abstractions;
using Cleanse.Storage.InMemory;

namespace Cleanse.Core.Test.Storage;

public sealed class InMemoryIssueStoreTests : IssueStoreContractTests
{
    protected override IIssueStore CreateStore() => new InMemoryIssueStore();
}

public sealed class InMemoryAuditStoreTests : AuditStoreContractTests
{
    protected override IAuditStore CreateStore() => new InMemoryAuditStore();
}

public sealed class InMemoryIssueHistoryStoreTests : IssueHistoryStoreContractTests
{
    protected override IIssueHistoryStore CreateStore() => new InMemoryIssueHistoryStore();
}

public sealed class InMemoryRuleRunStoreTests : RuleRunStoreContractTests
{
    protected override IRuleRunStore CreateStore() => new InMemoryRuleRunStore();
}
