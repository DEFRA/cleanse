using Cleanse.Core.Domain;

namespace Cleanse.Core.Contracts;

/// <summary>Read model for an issue. Statuses are wire strings, not enums, so the eventual API
/// contract is stable even if the enum members are reordered.</summary>
public sealed record IssueDto(
    string Id,
    string GroupId,
    string RuleId,
    string RunId,
    string Status,
    string WorkflowStatus,
    string? AssignedTo,
    string? Notes,
    string? ExternalNotes,
    string? Priority,
    DateTimeOffset? ExternalReceivedAtUtc,
    DateTimeOffset? SuppressionExpiresAtUtc,
    IReadOnlyDictionary<string, string> Context,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ActivatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc,
    int Version,
    Guid? AssignedUserId = null,
    string? AssignedUserFullName = null);

/// <summary>
/// Read model for a run. Progress, elapsed time and the projected completion are derived at
/// read time rather than stored, so a running run reports a current answer on every request.
/// </summary>
public sealed record RuleRunDto(
    string RunId,
    string GroupId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? AbandonReason,
    int DetectionsProcessed,
    int IssuesCreated,
    int IssuesReactivated,
    int IssuesUpdated,
    int IssuesUnchanged,
    int IssuesDeactivated,
    string? CurrentStage,
    int RecordsProcessed,
    int RecordsTotal,
    double ProgressPercent,
    double ElapsedSeconds,
    DateTimeOffset? EstimatedCompletionAtUtc,
    DateTimeOffset? HeartbeatAtUtc,
    bool CancellationRequested,
    string? FailureMessage,
    string? FailureDetail);

/// <summary>
/// List projection for a run. Carries everything the detail view does except the failure stack
/// dump, which can run to kilobytes and has no place in a page of fifty rows.
/// </summary>
public sealed record RuleRunSummaryDto(
    string RunId,
    string GroupId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? AbandonReason,
    int DetectionsProcessed,
    int IssuesCreated,
    int IssuesReactivated,
    int IssuesUpdated,
    int IssuesUnchanged,
    int IssuesDeactivated,
    string? CurrentStage,
    int RecordsProcessed,
    int RecordsTotal,
    double ProgressPercent,
    double ElapsedSeconds,
    DateTimeOffset? EstimatedCompletionAtUtc,
    DateTimeOffset? HeartbeatAtUtc,
    bool CancellationRequested,
    string? FailureMessage);

public sealed record AuditChangeDto(string Field, string? From, string? To);

public sealed record AuditEntryDto(
    Guid Id,
    string? IssueId,
    string? RunId,
    string Kind,
    string ActorId,
    string ActorDisplayName,
    string ActorKind,
    DateTimeOffset AtUtc,
    IReadOnlyList<AuditChangeDto> Changes);

public sealed record IssueHistoryEntryDto(
    Guid Id,
    string IssueId,
    string ActionType,
    string Comments,
    DateTimeOffset CreatedAtUtc,
    string CreatedById,
    string CreatedByDisplayName);

public sealed record IssueStatusCountDto(string Status, string WorkflowStatus, int Count);

/// <summary>
/// One dependency, as a diagnostics page wants to render it: what it is, where it points, and
/// whether it answered. <see cref="State"/> is <c>ok</c>, <c>failed</c> or <c>unknown</c>.
/// </summary>
public sealed record DependencyDto(
    string Provider,
    string? Target,
    string State,
    string? Detail,
    double ElapsedMilliseconds);

public sealed record IssueCountsDto(
    string State,
    string? Detail,
    int Total,
    IReadOnlyList<IssueStatusCountDto> Breakdown);

/// <summary>
/// Everything an operator needs to see at a glance. <see cref="Service"/> says which build is
/// answering, <see cref="Database"/> is the issue store, <see cref="Storage"/> is where uploaded
/// import files are kept, <see cref="Krds"/> is whether the source the rules engine reads answers,
/// and <see cref="KrdsAccess"/> is whether it can actually be read.
/// </summary>
public sealed record DiagnosticsDto(
    ServiceInfoDto Service,
    DependencyDto Database,
    DependencyDto Storage,
    DependencyDto Krds,
    DependencyDto KrdsAccess,
    IssueCountsDto Issues,
    DateTimeOffset? LastResetAtUtc);

/// <summary>
/// Which build is answering, and where. <see cref="Version"/> and <see cref="Environment"/> come
/// from SERVICE_VERSION and ENVIRONMENT, which CDP injects into every container; both are null
/// when unset, as they are when running locally.
/// </summary>
public sealed record ServiceInfoDto(
    string Name,
    string? Version,
    string? Environment,
    string Runtime,
    DateTimeOffset StartedAtUtc);

public sealed record GroupSummaryDto(
    string GroupId,
    RuleRunDto? LatestRun,
    int TotalIssues,
    int ActiveIssues,
    int InactiveIssues,
    IReadOnlyList<IssueStatusCountDto> Breakdown);

/// <summary>What a batch of detections did. Lets a rules engine log meaningful progress.</summary>
public sealed record DetectionUpsertResult(
    int DetectionsProcessed,
    int Created,
    int Reactivated,
    int Updated,
    int Unchanged)
{
    public static readonly DetectionUpsertResult Empty = new(0, 0, 0, 0, 0);

    public DetectionUpsertResult Add(DetectionUpsertResult other) => new(
        DetectionsProcessed + other.DetectionsProcessed,
        Created + other.Created,
        Reactivated + other.Reactivated,
        Updated + other.Updated,
        Unchanged + other.Unchanged);

    internal RuleRunCounters ToCounters() =>
        new(DetectionsProcessed, Created, Reactivated, Updated, Unchanged, 0);
}

/// <summary>How many lapsed suppressions the scheduled sweep returned to the triage queue.</summary>
public sealed record SuppressionExpiryResult(int Expired);

/// <summary>What a reset destroyed, and over what range.</summary>
public sealed record ResetResultDto(
    string Scope,
    int Issues,
    int IssueHistoryEntries,
    int AuditEntries,
    int RuleRuns,
    int ImportJobs,
    int ImportRejections)
{
    /// <summary>The <see cref="Scope"/> reported when no group was named.</summary>
    public const string AllGroups = "all";
}
