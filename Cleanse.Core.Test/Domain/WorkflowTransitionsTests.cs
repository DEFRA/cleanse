using Cleanse.Core.Domain;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Domain;

public class WorkflowTransitionsTests
{
    public static TheoryData<WorkflowStatus, WorkflowStatus, bool> Matrix()
    {
        var expected = new Dictionary<WorkflowStatus, WorkflowStatus[]>
        {
            [WorkflowStatus.Todo] =
            [
                WorkflowStatus.InProgress, WorkflowStatus.Resolved, WorkflowStatus.Ignored,
                WorkflowStatus.Suppressed, WorkflowStatus.Nonactionable
            ],
            [WorkflowStatus.InProgress] =
            [
                WorkflowStatus.Todo, WorkflowStatus.Resolved, WorkflowStatus.Ignored,
                WorkflowStatus.Suppressed, WorkflowStatus.Nonactionable
            ],
            [WorkflowStatus.Suppressed] =
                [WorkflowStatus.Todo, WorkflowStatus.InProgress, WorkflowStatus.Resolved, WorkflowStatus.Ignored],
            [WorkflowStatus.Resolved] =
                [WorkflowStatus.Todo, WorkflowStatus.InProgress, WorkflowStatus.Ignored],
            [WorkflowStatus.Ignored] =
                [WorkflowStatus.Todo, WorkflowStatus.InProgress],

            // Terminal like Resolved: reopenable, but not reclassifiable straight to another outcome.
            [WorkflowStatus.Nonactionable] =
                [WorkflowStatus.Todo, WorkflowStatus.InProgress]
        };

        var data = new TheoryData<WorkflowStatus, WorkflowStatus, bool>();
        foreach (var from in Enum.GetValues<WorkflowStatus>())
        {
            foreach (var to in Enum.GetValues<WorkflowStatus>())
            {
                data.Add(from, to, from == to || expected[from].Contains(to));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_pair_matches_the_agreed_matrix(WorkflowStatus from, WorkflowStatus to, bool allowed) =>
        Assert.Equal(allowed, WorkflowTransitions.IsAllowed(from, to));

    [Fact]
    public void A_resolved_issue_cannot_be_suppressed_directly()
    {
        Assert.False(WorkflowTransitions.IsAllowed(WorkflowStatus.Resolved, WorkflowStatus.Suppressed));
        Assert.False(WorkflowTransitions.IsAllowed(WorkflowStatus.Ignored, WorkflowStatus.Suppressed));
    }

    [Theory]
    [InlineData(WorkflowStatus.Resolved, WorkflowStatus.Suppressed)]
    [InlineData(WorkflowStatus.Ignored, WorkflowStatus.Resolved)]
    public void Disallowed_transitions_are_rejected_by_the_aggregate(WorkflowStatus from, WorkflowStatus to)
    {
        var issue = TestData.NewIssue("12/345/6789");
        if (from == WorkflowStatus.Suppressed)
        {
            Assert.True(issue.Suppress(TestData.Night1.AddDays(1), TestData.Night1).IsSuccess);
        }
        else
        {
            Assert.True(issue.ChangeWorkflowStatus(from, TestData.Night1).IsSuccess);
        }

        var result = issue.ChangeWorkflowStatus(to, TestData.Night1);

        Assert.True(result.IsFailure);
        Assert.Equal(from, issue.WorkflowStatus);
    }

    [Fact]
    public void A_no_op_transition_is_allowed_but_immaterial()
    {
        var issue = TestData.NewIssue("12/345/6789");
        Assert.True(issue.ChangeWorkflowStatus(WorkflowStatus.InProgress, TestData.Night1).IsSuccess);
        var version = issue.Version;

        var result = issue.ChangeWorkflowStatus(WorkflowStatus.InProgress, TestData.Night1.AddDays(1));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsMaterial);
        Assert.Equal(version, issue.Version);
    }
}
