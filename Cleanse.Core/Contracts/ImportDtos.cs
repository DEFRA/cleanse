using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Contracts;

/// <summary>
/// One row of an external extract, already parsed and normalised. Carries only human-axis state
/// plus the contact dates, so an import can never disturb what the rules engine owns.
/// </summary>
public sealed record ExternalIssueRow
{
    public required long RowNumber { get; init; }

    public required IssueId IssueId { get; init; }

    public WorkflowStatus? WorkflowStatus { get; init; }

    public string? ExternalNotes { get; init; }

    public bool HasAssignee { get; init; }

    public string? AssignedTo { get; init; }

    public IssuePriority? Priority { get; init; }

    public DateTimeOffset? ReceivedAtUtc { get; init; }

    /// <summary>Dated contact events, mapped straight from the source columns with no inference
    /// about what the contact actually was.</summary>
    public IReadOnlyList<ExternalContactEvent> Contacts { get; init; } = [];

    internal ExternalIssueState ToState() => new()
    {
        WorkflowStatus = WorkflowStatus,
        ExternalNotes = ExternalNotes,
        Priority = Priority,
        ReceivedAtUtc = ReceivedAtUtc
    };
}

public sealed record ExternalContactEvent(string Column, HistoryActionType ActionType, DateTimeOffset AtUtc);

public sealed record ImportBatchResult(
    int RowsProcessed,
    int Matched,
    int Unmatched,
    int HistoryEntriesWritten)
{
    public static readonly ImportBatchResult Empty = new(0, 0, 0, 0);
}

public sealed record ImportJobDto(
    Guid Id,
    string GroupId,
    string FileName,
    string Status,
    int TotalRows,
    int ProcessedRows,
    int PercentComplete,
    int Matched,
    int Unmatched,
    int DuplicatesCollapsed,
    int Invalid,
    string RequestedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? FailureReason);

public sealed record ImportRejectionDto(long RowNumber, string? IssueId, string Reason, string Detail);
