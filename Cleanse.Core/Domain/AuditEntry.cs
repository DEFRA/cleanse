namespace Cleanse.Core.Domain;

public enum AuditEventKind
{
    None,
    IssueCreated,
    IssueReactivated,
    IssueDeactivated,
    IssueContextChanged,
    IssueAssigned,
    IssueWorkflowStatusChanged,
    IssuePriorityChanged,
    IssueExternalStateApplied,
    IssueSuppressed,
    IssueSuppressionExpired,
    IssueNotesChanged,
    IssueHistoryAdded,
    RunStarted,
    RunCompleted,
    RunAbandoned,
    RunCancellationRequested,
    RunCancelled,
    RunFailed
}

/// <summary>A single field-level change: what it was, what it became.</summary>
public sealed record AuditChange(string Field, string? From, string? To);

/// <summary>
/// An append-only record of a material change. Run lifecycle events carry a null
/// <see cref="IssueId"/>; the end-of-run sweep records one of these for the whole run rather
/// than one per deactivated issue.
/// </summary>
public sealed record AuditEntry
{
    private AuditEntry(
        Guid id,
        IssueId? issueId,
        string? runId,
        AuditEventKind kind,
        Actor actor,
        DateTimeOffset atUtc,
        IReadOnlyList<AuditChange> changes)
    {
        Id = id;
        IssueId = issueId;
        RunId = runId;
        Kind = kind;
        Actor = actor;
        AtUtc = atUtc;
        Changes = changes;
    }

    public Guid Id { get; }

    public IssueId? IssueId { get; }

    public string? RunId { get; }

    public AuditEventKind Kind { get; }

    public Actor Actor { get; }

    public DateTimeOffset AtUtc { get; }

    public IReadOnlyList<AuditChange> Changes { get; }

    public static AuditEntry ForIssue(
        IssueId issueId,
        IssueChange change,
        Actor actor,
        DateTimeOffset atUtc,
        string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(Guid.CreateVersion7(atUtc), issueId, runId, change.Kind, actor, atUtc, change.Changes);
    }

    public static AuditEntry ForRun(
        string runId,
        AuditEventKind kind,
        Actor actor,
        DateTimeOffset atUtc,
        IReadOnlyList<AuditChange>? changes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(Guid.CreateVersion7(atUtc), null, runId, kind, actor, atUtc, changes ?? []);
    }

    public static AuditEntry Rehydrate(
        Guid id,
        IssueId? issueId,
        string? runId,
        AuditEventKind kind,
        Actor actor,
        DateTimeOffset atUtc,
        IReadOnlyList<AuditChange> changes) =>
        new(id, issueId, runId, kind, actor, atUtc, changes);
}
