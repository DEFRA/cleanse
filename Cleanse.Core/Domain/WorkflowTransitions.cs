namespace Cleanse.Core.Domain;

/// <summary>
/// The legal human triage transitions. The machine-owned <see cref="IssueStatus"/> axis is
/// deliberately not modelled here: a re-detection never moves an issue along this axis.
/// </summary>
public static class WorkflowTransitions
{
    private static readonly Dictionary<WorkflowStatus, WorkflowStatus[]> Allowed = new()
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
        [
            WorkflowStatus.Todo, WorkflowStatus.InProgress, WorkflowStatus.Resolved, WorkflowStatus.Ignored
        ],
        [WorkflowStatus.Resolved] =
        [
            WorkflowStatus.Todo, WorkflowStatus.InProgress, WorkflowStatus.Ignored
        ],
        [WorkflowStatus.Ignored] =
        [
            WorkflowStatus.Todo, WorkflowStatus.InProgress
        ],
        [WorkflowStatus.Nonactionable] =
        [
            WorkflowStatus.Todo, WorkflowStatus.InProgress
        ]
    };

    public static bool IsAllowed(WorkflowStatus from, WorkflowStatus to) =>
        from == to || Array.IndexOf(Allowed[from], to) >= 0;

    public static IReadOnlyList<WorkflowStatus> AllowedFrom(WorkflowStatus from) => Allowed[from];
}
