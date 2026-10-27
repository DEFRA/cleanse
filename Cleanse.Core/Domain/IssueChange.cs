namespace Cleanse.Core.Domain;

/// <summary>
/// The outcome of a mutation attempt on an <see cref="Issue"/>. A change with no entries is
/// immaterial and must not produce an audit entry or bump the version — this is what keeps a
/// nightly re-detection that alters nothing but the run id out of the audit trail.
/// </summary>
public sealed record IssueChange(AuditEventKind Kind, IReadOnlyList<AuditChange> Changes)
{
    public static readonly IssueChange None = new(AuditEventKind.None, []);

    public bool IsMaterial => Changes.Count > 0;
}

/// <summary>
/// A snapshot of what an external system of record says about an issue. Only ever carries
/// human-axis state: there is no way to express a change to Status, RunId or GroupId here.
/// A null member means "the source says nothing", not "clear it".
/// </summary>
public sealed record ExternalIssueState
{
    public WorkflowStatus? WorkflowStatus { get; init; }

    public string? ExternalNotes { get; init; }

    public IssuePriority? Priority { get; init; }

    public DateTimeOffset? ReceivedAtUtc { get; init; }
}

/// <summary>What a rule emits when it fires. The group is inherited from the run.</summary>
public sealed record IssueDetection
{
    public IssueDetection(IssueId id, string ruleId, IReadOnlyDictionary<string, string>? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        if (!id.HasValue)
        {
            throw new ArgumentException("A detection must carry an issue id.", nameof(id));
        }

        Id = id;
        RuleId = ruleId.Trim();
        Context = IssueContext.From(context);
    }

    public IssueId Id { get; }

    public string RuleId { get; }

    public IssueContext Context { get; }
}

/// <summary>
/// The persistence-facing projection of an <see cref="Issue"/>. Storage adapters map this
/// rather than reaching into the aggregate, so the aggregate's invariants cannot be bypassed.
/// </summary>
public sealed record IssueSnapshot(
    string Id,
    string GroupId,
    string RuleId,
    string RunId,
    IssueStatus Status,
    WorkflowStatus WorkflowStatus,
    string? AssignedTo,
    string? Notes,
    string? ExternalNotes,
    IssuePriority? Priority,
    DateTimeOffset? ExternalReceivedAtUtc,
    DateTimeOffset? SuppressionExpiresAtUtc,
    IssueContext Context,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ActivatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc,
    int Version,
    Guid? AssignedUserId = null,
    string? AssignedUserFullName = null);
