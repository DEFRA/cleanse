namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// The physical row shapes. Deliberately separate from the domain snapshots: these carry
/// storage-only concerns such as the identity <c>Sequence</c> column, and keep jsonb payloads as
/// raw text so Npgsql maps them directly without a value converter.
/// </summary>
internal sealed class IssueRow
{
    public required string Id { get; set; }

    public required string GroupId { get; set; }

    public required string RuleId { get; set; }

    public required string RunId { get; set; }

    public required string Status { get; set; }

    public required string WorkflowStatus { get; set; }

    public string? AssignedTo { get; set; }

    public string? Notes { get; set; }

    public string? ExternalNotes { get; set; }

    public string? Priority { get; set; }

    public DateTimeOffset? ExternalReceivedAtUtc { get; set; }

    public DateTimeOffset? SuppressionExpiresAtUtc { get; set; }

    public required string Context { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset ActivatedAtUtc { get; set; }

    public DateTimeOffset? DeactivatedAtUtc { get; set; }

    public int Version { get; set; }
}

/// <summary>One recorded reset. Survives the reset it records.</summary>
internal sealed class DataResetRow
{
    public long Sequence { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public required string Scope { get; set; }

    public required string ActorId { get; set; }

    public int Issues { get; set; }
}

internal sealed class RuleRunRow
{
    public required string RunId { get; set; }

    public required string GroupId { get; set; }

    public required string State { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public string? AbandonReason { get; set; }

    public int DetectionsProcessed { get; set; }

    public int IssuesCreated { get; set; }

    public int IssuesReactivated { get; set; }

    public int IssuesUpdated { get; set; }

    public int IssuesUnchanged { get; set; }

    public int IssuesDeactivated { get; set; }

    public string? CurrentStage { get; set; }

    public int RecordsProcessed { get; set; }

    public int RecordsTotal { get; set; }

    public DateTimeOffset? HeartbeatAtUtc { get; set; }

    public bool CancellationRequested { get; set; }

    public string? FailureMessage { get; set; }

    public string? FailureDetail { get; set; }

    public int Version { get; set; }
}

internal sealed class AuditEntryRow
{
    public Guid Id { get; set; }

    /// <summary>Database-generated. The stable tie-break for entries sharing a timestamp.</summary>
    public long Sequence { get; set; }

    public string? IssueId { get; set; }

    public string? RunId { get; set; }

    public required string Kind { get; set; }

    public required string ActorId { get; set; }

    public required string ActorDisplayName { get; set; }

    public required string ActorKind { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public required string Changes { get; set; }
}

internal sealed class IssueHistoryRow
{
    public Guid Id { get; set; }

    public long Sequence { get; set; }

    public required string IssueId { get; set; }

    public required string ActionType { get; set; }

    public required string Comments { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public required string CreatedById { get; set; }

    public required string CreatedByDisplayName { get; set; }

    public required string CreatedByKind { get; set; }
}

internal sealed class ImportJobRow
{
    public Guid Id { get; set; }

    public required string GroupId { get; set; }

    public required string FileName { get; set; }

    public required string BlobKey { get; set; }

    public required string Status { get; set; }

    public int TotalRows { get; set; }

    public int ProcessedRows { get; set; }

    public int Matched { get; set; }

    public int Unmatched { get; set; }

    public int DuplicatesCollapsed { get; set; }

    public int Invalid { get; set; }

    public required string RequestedBy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public DateTimeOffset? HeartbeatAtUtc { get; set; }

    public string? FailureReason { get; set; }

    public int Version { get; set; }
}

internal sealed class ImportJobRejectionRow
{
    public long Sequence { get; set; }

    public Guid ImportJobId { get; set; }

    public long RowNumber { get; set; }

    public string? IssueId { get; set; }

    public required string Reason { get; set; }

    public required string Detail { get; set; }
}
