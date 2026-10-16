using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Queries.Runs;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Queries;

public class IssueQueryTests
{
    private static async Task<CleanseHarness> SeededHarnessAsync()
    {
        var harness = new CleanseHarness();
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-1"));
        await harness.SendAsync(new UpsertDetectionsCommand(
            "run-1",
            [
                TestData.Detection("a", TestData.Rule2, ("keeper", "J Smith")),
                TestData.Detection("b", TestData.Rule2, ("keeper", "A Jones")),
                TestData.Detection("c", TestData.Rule5, ("keeper", "J Smith"))
            ],
            Actor.System));
        return harness;
    }

    [Fact]
    public async Task An_issue_is_returned_as_a_dto_with_wire_statuses()
    {
        using var harness = await SeededHarnessAsync();

        var issue = await harness.QueryAsync(new GetIssueQuery(TestData.Key("a")));

        Assert.Equal("active", issue.Status);
        Assert.Equal("todo", issue.WorkflowStatus);
        Assert.Equal(TestData.Group, issue.GroupId);
        Assert.Equal("J Smith", issue.Context["keeper"]);
    }

    [Fact]
    public async Task A_missing_issue_reports_not_found()
    {
        using var harness = await SeededHarnessAsync();

        var result = await harness.TryQueryAsync(new GetIssueQuery(TestData.Key("ghost")));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.IssueNotFound, result.Error.Code);
    }

    [Fact]
    public async Task Search_narrows_by_rule()
    {
        using var harness = await SeededHarnessAsync();

        var page = await harness.QueryAsync(new SearchIssuesQuery(new IssueFilter { RuleId = TestData.Rule5 }));

        Assert.Equal(TestData.Key("c", TestData.Rule5).Value, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_narrows_by_context()
    {
        using var harness = await SeededHarnessAsync();

        var page = await harness.QueryAsync(new SearchIssuesQuery(new IssueFilter
        {
            ContextMatches = new Dictionary<string, string> { ["keeper"] = "J Smith" }
        }));

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Search_finds_the_unassigned_queue()
    {
        using var harness = await SeededHarnessAsync();
        await harness.SendAsync(new AssignIssueCommand(TestData.Key("a"), "alice", TestData.Alice));

        var queue = await harness.QueryAsync(new SearchIssuesQuery(new IssueFilter { Unassigned = true }));
        var mine = await harness.QueryAsync(new SearchIssuesQuery(new IssueFilter { AssignedTo = "alice" }));

        Assert.Equal(2, queue.TotalCount);
        Assert.Equal(1, mine.TotalCount);
    }

    [Fact]
    public async Task Search_pages()
    {
        using var harness = await SeededHarnessAsync();

        var page = await harness.QueryAsync(new SearchIssuesQuery(new IssueFilter
        {
            Page = new PageRequest(2, 2)
        }));

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task The_group_summary_counts_both_axes()
    {
        using var harness = await SeededHarnessAsync();
        await harness.SendAsync(new SetIssueWorkflowStatusCommand(
            TestData.Key("a"), WorkflowStatus.Resolved, TestData.Alice));
        await harness.SendAsync(new CompleteRunCommand("run-1", Actor.System));

        harness.Clock.AdvanceDays(1);
        await harness.SendAsync(new StartRunCommand(TestData.Group, Actor.System, "run-2"));
        await harness.SendAsync(new UpsertDetectionsCommand("run-2", [TestData.Detection("a")], Actor.System));
        await harness.SendAsync(new CompleteRunCommand("run-2", Actor.System));

        var summary = await harness.QueryAsync(new GetGroupSummaryQuery(TestData.Group));

        Assert.Equal(3, summary.TotalIssues);
        Assert.Equal(1, summary.ActiveIssues);
        Assert.Equal(2, summary.InactiveIssues);
        Assert.Equal("run-2", summary.LatestRun!.RunId);
        Assert.Equal(1, summary.Breakdown.Single(b => b is
            { Status: "active", WorkflowStatus: "resolved" }).Count);
    }

    [Fact]
    public async Task A_group_with_nothing_in_it_summarises_as_empty()
    {
        using var harness = new CleanseHarness();

        var summary = await harness.QueryAsync(new GetGroupSummaryQuery("empty-group"));

        Assert.Equal(0, summary.TotalIssues);
        Assert.Null(summary.LatestRun);
        Assert.Empty(summary.Breakdown);
    }
}
