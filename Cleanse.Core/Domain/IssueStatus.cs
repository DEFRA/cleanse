namespace Cleanse.Core.Domain;

/// <summary>
/// Machine-owned lifecycle state. Only the rules engine moves an issue between these:
/// <see cref="Active"/> means the most recent run for the group evaluated the rule to true.
/// </summary>
public enum IssueStatus
{
    Active,
    Inactive
}

/// <summary>
/// Human-owned triage state, orthogonal to <see cref="IssueStatus"/>. A re-detection never
/// changes it.
/// </summary>
public enum WorkflowStatus
{
    Todo,
    InProgress,
    Suppressed,
    Resolved,
    Ignored,

    /// <summary>Terminal: looked at, and nothing can be done. Distinct from resolved.</summary>
    Nonactionable
}

/// <summary>Triage urgency. Nullable on the issue; absent means not yet prioritised.</summary>
public enum IssuePriority
{
    Low,
    Medium,
    High
}

public static class StatusNames
{
    public const string Active = "active";
    public const string Inactive = "inactive";

    public const string Todo = "todo";
    public const string InProgress = "in-progress";
    public const string Suppressed = "suppressed";
    public const string Resolved = "resolved";
    public const string Ignored = "ignored";
    public const string Nonactionable = "nonactionable";

    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    public static string ToWire(this IssuePriority priority) => priority switch
    {
        IssuePriority.Low => Low,
        IssuePriority.Medium => Medium,
        IssuePriority.High => High,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null)
    };

    public static bool TryParsePriority(string? value, out IssuePriority priority)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Low:
                priority = IssuePriority.Low;
                return true;
            case Medium:
                priority = IssuePriority.Medium;
                return true;
            case High:
                priority = IssuePriority.High;
                return true;
            default:
                priority = default;
                return false;
        }
    }

    public static string ToWire(this IssueStatus status) => status switch
    {
        IssueStatus.Active => Active,
        IssueStatus.Inactive => Inactive,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string ToWire(this WorkflowStatus status) => status switch
    {
        WorkflowStatus.Todo => Todo,
        WorkflowStatus.InProgress => InProgress,
        WorkflowStatus.Suppressed => Suppressed,
        WorkflowStatus.Resolved => Resolved,
        WorkflowStatus.Ignored => Ignored,
        WorkflowStatus.Nonactionable => Nonactionable,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParseIssueStatus(string? value, out IssueStatus status)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Active:
                status = IssueStatus.Active;
                return true;
            case Inactive:
                status = IssueStatus.Inactive;
                return true;
            default:
                status = default;
                return false;
        }
    }

    public static bool TryParseWorkflowStatus(string? value, out WorkflowStatus status)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Todo:
                status = WorkflowStatus.Todo;
                return true;
            case InProgress:
                status = WorkflowStatus.InProgress;
                return true;
            case Suppressed:
                status = WorkflowStatus.Suppressed;
                return true;
            case Resolved:
                status = WorkflowStatus.Resolved;
                return true;
            case Ignored:
                status = WorkflowStatus.Ignored;
                return true;
            case Nonactionable:
                status = WorkflowStatus.Nonactionable;
                return true;
            default:
                status = default;
                return false;
        }
    }
}
