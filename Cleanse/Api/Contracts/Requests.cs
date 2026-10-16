using System.ComponentModel.DataAnnotations;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Contracts;

/// <summary>
/// The actor is taken from the authenticated caller, never from the request body, so a caller
/// cannot attribute their change to somebody else.
/// </summary>
public sealed record AssignIssueRequest(string? AssignTo, int? ExpectedVersion);

public sealed record SetNotesRequest(string? Notes, int? ExpectedVersion);

public sealed record SetWorkflowStatusRequest(
    [property: Required] string WorkflowStatus,
    int? ExpectedVersion);

public sealed record SuppressIssueRequest(
    [property: Required] DateTimeOffset ExpiresAtUtc,
    int? ExpectedVersion);

public sealed record AddHistoryRequest(
    [property: Required] string ActionType,
    [property: Required, MaxLength(4000)] string Comments);

public sealed record StartRunRequest(
    [property: Required] string GroupId,
    string? RunId);

public sealed record AbandonRunRequest([property: Required, MaxLength(500)] string Reason);

public sealed record DetectionRequest(
    [property: Required] string Id,
    [property: Required] string RuleId,
    IReadOnlyDictionary<string, string>? Context)
{
    public IssueDetection ToDetection() => new(IssueId.From(Id), RuleId, Context);
}

public sealed record UpsertDetectionsRequest(
    [property: Required] IReadOnlyList<DetectionRequest> Detections);

/// <summary>Omitting GroupId clears every group. There is no undo.</summary>
public sealed record ResetRequest(string? GroupId);
