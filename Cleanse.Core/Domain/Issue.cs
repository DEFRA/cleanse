namespace Cleanse.Core.Domain;

/// <summary>
/// The aggregate root. Two independent axes of state live here: <see cref="Status"/>, owned by
/// the rules engine, and <see cref="WorkflowStatus"/>, owned by humans. The detection path
/// (<see cref="RecordDetection"/>) is physically unable to write a human-owned field, which is
/// what guarantees a nightly run can never discard someone's triage decision.
/// </summary>
public sealed class Issue
{
    private Issue(
        IssueId id,
        string groupId,
        string ruleId,
        string runId,
        IssueStatus status,
        WorkflowStatus workflowStatus,
        string? assignedTo,
        string? notes,
        string? externalNotes,
        IssuePriority? priority,
        DateTimeOffset? externalReceivedAtUtc,
        DateTimeOffset? suppressionExpiresAtUtc,
        IssueContext context,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset activatedAtUtc,
        DateTimeOffset? deactivatedAtUtc,
        int version,
        Guid? assignedUserId = null,
        string? assignedUserFullName = null)
    {
        Id = id;
        GroupId = groupId;
        RuleId = ruleId;
        RunId = runId;
        Status = status;
        WorkflowStatus = workflowStatus;
        AssignedTo = assignedTo;
        AssignedUserId = assignedUserId;
        AssignedUserFullName = assignedUserFullName;
        Notes = notes;
        ExternalNotes = externalNotes;
        Priority = priority;
        ExternalReceivedAtUtc = externalReceivedAtUtc;
        SuppressionExpiresAtUtc = suppressionExpiresAtUtc;
        Context = context;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        ActivatedAtUtc = activatedAtUtc;
        DeactivatedAtUtc = deactivatedAtUtc;
        Version = version;
    }

    public IssueId Id { get; }

    public string GroupId { get; }

    public string RuleId { get; }

    public string RunId { get; private set; }

    public IssueStatus Status { get; private set; }

    public WorkflowStatus WorkflowStatus { get; private set; }

    public string? AssignedTo { get; private set; }

    public Guid? AssignedUserId { get; private set; }

    /// <summary>The known user's display name at assignment time; never an identity key.</summary>
    public string? AssignedUserFullName { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Mirrored from the external system of record. Replaced wholesale on each sync,
    /// never appended, so repeated syncs converge rather than accumulate.</summary>
    public string? ExternalNotes { get; private set; }

    public IssuePriority? Priority { get; private set; }

    /// <summary>When the external system first raised this. Distinct from <see cref="CreatedAtUtc"/>,
    /// which records when the rules engine first detected it.</summary>
    public DateTimeOffset? ExternalReceivedAtUtc { get; private set; }

    public DateTimeOffset? SuppressionExpiresAtUtc { get; private set; }

    public IssueContext Context { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>
    /// When <see cref="Status"/> last became <see cref="IssueStatus.Active"/>. Equal to
    /// <see cref="CreatedAtUtc"/> on first detection, and moved forward by every reactivation,
    /// so "how long has this been open this time" is answerable without replaying the audit.
    /// </summary>
    public DateTimeOffset ActivatedAtUtc { get; private set; }

    /// <summary>When the sweep last marked this inactive. Null whenever the issue is active.</summary>
    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    /// <summary>Incremented only by material changes, so a nightly run does not invalidate every
    /// in-flight human edit.</summary>
    public int Version { get; private set; }

    public bool IsSuppressionExpired(DateTimeOffset at) =>
        WorkflowStatus == WorkflowStatus.Suppressed &&
        SuppressionExpiresAtUtc is { } expiry &&
        expiry <= at;

    public static Issue FromDetection(IssueDetection detection, string groupId, string runId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return new Issue(
            detection.Id,
            groupId.Trim(),
            detection.RuleId,
            runId.Trim(),
            IssueStatus.Active,
            WorkflowStatus.Todo,
            assignedTo: null,
            notes: null,
            externalNotes: null,
            priority: null,
            externalReceivedAtUtc: null,
            suppressionExpiresAtUtc: null,
            detection.Context,
            now,
            now,
            activatedAtUtc: now,
            deactivatedAtUtc: null,
            version: 1);
    }

    public static Issue FromSnapshot(IssueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new Issue(
            IssueId.From(snapshot.Id),
            snapshot.GroupId,
            snapshot.RuleId,
            snapshot.RunId,
            snapshot.Status,
            snapshot.WorkflowStatus,
            snapshot.AssignedTo,
            snapshot.Notes,
            snapshot.ExternalNotes,
            snapshot.Priority,
            snapshot.ExternalReceivedAtUtc,
            snapshot.SuppressionExpiresAtUtc,
            snapshot.Context,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.ActivatedAtUtc,
            snapshot.DeactivatedAtUtc,
            snapshot.Version,
            snapshot.AssignedUserId,
            snapshot.AssignedUserFullName);
    }

    public IssueSnapshot ToSnapshot() => new(
        Id.Value,
        GroupId,
        RuleId,
        RunId,
        Status,
        WorkflowStatus,
        AssignedTo,
        Notes,
        ExternalNotes,
        Priority,
        ExternalReceivedAtUtc,
        SuppressionExpiresAtUtc,
        Context,
        CreatedAtUtc,
        UpdatedAtUtc,
        ActivatedAtUtc,
        DeactivatedAtUtc,
        Version,
        AssignedUserId,
        AssignedUserFullName);

    /// <summary>
    /// Re-detection by a later run. Touches only the run id, the active flag and the context;
    /// the human triage axis is deliberately left alone.
    /// </summary>
    public IssueChange RecordDetection(string runId, IssueContext context, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(context);

        // The run id always advances, otherwise the end-of-run sweep would deactivate this
        // issue; but on its own that is not a material change.
        RunId = runId.Trim();

        var changes = new List<AuditChange>();
        var kind = AuditEventKind.IssueContextChanged;

        if (Status == IssueStatus.Inactive)
        {
            changes.Add(new AuditChange(nameof(Status), Status.ToWire(), IssueStatus.Active.ToWire()));
            changes.Add(new AuditChange(nameof(ActivatedAtUtc), Format(ActivatedAtUtc), Format(now)));
            changes.Add(new AuditChange(nameof(DeactivatedAtUtc), Format(DeactivatedAtUtc), null));
            Status = IssueStatus.Active;
            ActivatedAtUtc = now;
            DeactivatedAtUtc = null;
            kind = AuditEventKind.IssueReactivated;
        }

        var contextChanges = context.DiffFrom(Context);
        if (contextChanges.Count > 0)
        {
            Context = context;
            changes.AddRange(contextChanges);
        }

        return Apply(kind, changes, now);
    }

    /// <summary>End-of-run sweep: the latest run did not re-detect this anomaly.</summary>
    public IssueChange Deactivate(DateTimeOffset now)
    {
        if (Status == IssueStatus.Inactive)
        {
            return IssueChange.None;
        }

        var change = new AuditChange(nameof(Status), Status.ToWire(), IssueStatus.Inactive.ToWire());
        var deactivated = new AuditChange(nameof(DeactivatedAtUtc), Format(DeactivatedAtUtc), Format(now));
        Status = IssueStatus.Inactive;
        DeactivatedAtUtc = now;
        return Apply(AuditEventKind.IssueDeactivated, [change, deactivated], now);
    }

    public IssueChange AssignTo(string? assignee, DateTimeOffset now) =>
        Assign(assignee, null, null, now);

    public IssueChange AssignKnownUser(KnownUser? user, DateTimeOffset now) =>
        Assign(user?.FullName, user?.Id, user?.FullName, now);

    private IssueChange Assign(string? assignee, Guid? userId, string? fullName, DateTimeOffset now)
    {
        var normalised = string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim();
        var changes = new List<AuditChange>();
        if (!string.Equals(normalised, AssignedTo, StringComparison.Ordinal))
        {
            changes.Add(new AuditChange(nameof(AssignedTo), AssignedTo, normalised));
        }

        if (AssignedUserId != userId)
        {
            changes.Add(new AuditChange(nameof(AssignedUserId), AssignedUserId?.ToString(), userId?.ToString()));
        }

        if (!string.Equals(AssignedUserFullName, fullName, StringComparison.Ordinal))
        {
            changes.Add(new AuditChange(nameof(AssignedUserFullName), AssignedUserFullName, fullName));
        }

        AssignedTo = normalised;
        AssignedUserId = userId;
        AssignedUserFullName = fullName;
        return Apply(AuditEventKind.IssueAssigned, changes, now);
    }

    public IssueChange SetNotes(string? notes, DateTimeOffset now)
    {
        var normalised = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (string.Equals(normalised, Notes, StringComparison.Ordinal))
        {
            return IssueChange.None;
        }

        var change = new AuditChange(nameof(Notes), Notes, normalised);
        Notes = normalised;
        return Apply(AuditEventKind.IssueNotesChanged, [change], now);
    }

    public IssueChange SetPriority(IssuePriority? priority, DateTimeOffset now)
    {
        if (priority == Priority)
        {
            return IssueChange.None;
        }

        var change = new AuditChange(nameof(Priority), Priority?.ToWire(), priority?.ToWire());
        Priority = priority;
        return Apply(AuditEventKind.IssuePriorityChanged, [change], now);
    }

    /// <summary>
    /// Mirrors state from an external system of record. Deliberately bypasses
    /// <see cref="WorkflowTransitions"/>: that matrix governs what a caseworker may do inside
    /// Cleanse, and must not reject a faithful copy of what the source system already says.
    /// Machine-owned fields are untouched, so a sync cannot disturb what the rules engine found.
    /// </summary>
    public IssueChange ApplyExternalState(ExternalIssueState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        var changes = new List<AuditChange>();

        if (state.WorkflowStatus is { } workflowStatus && workflowStatus != WorkflowStatus)
        {
            changes.Add(new AuditChange(
                nameof(WorkflowStatus), WorkflowStatus.ToWire(), workflowStatus.ToWire()));
            WorkflowStatus = workflowStatus;

            // Leaving suppression by any route clears the expiry, exactly as the human path does.
            if (workflowStatus != WorkflowStatus.Suppressed && SuppressionExpiresAtUtc is not null)
            {
                changes.Add(new AuditChange(
                    nameof(SuppressionExpiresAtUtc), Format(SuppressionExpiresAtUtc), null));
                SuppressionExpiresAtUtc = null;
            }
        }

        var externalNotes = string.IsNullOrWhiteSpace(state.ExternalNotes) ? null : state.ExternalNotes.Trim();
        if (!string.Equals(externalNotes, ExternalNotes, StringComparison.Ordinal))
        {
            changes.Add(new AuditChange(nameof(ExternalNotes), ExternalNotes, externalNotes));
            ExternalNotes = externalNotes;
        }

        if (state.Priority != Priority)
        {
            changes.Add(new AuditChange(nameof(Priority), Priority?.ToWire(), state.Priority?.ToWire()));
            Priority = state.Priority;
        }

        if (state.ReceivedAtUtc != ExternalReceivedAtUtc)
        {
            changes.Add(new AuditChange(
                nameof(ExternalReceivedAtUtc),
                Format(ExternalReceivedAtUtc),
                Format(state.ReceivedAtUtc)));
            ExternalReceivedAtUtc = state.ReceivedAtUtc;
        }

        return Apply(AuditEventKind.IssueExternalStateApplied, changes, now);
    }

    public Result<IssueChange> ChangeWorkflowStatus(WorkflowStatus target, DateTimeOffset now)
    {
        if (target == WorkflowStatus.Suppressed)
        {
            return DomainErrors.Validation(
                $"Use {nameof(Suppress)} to move issue '{Id}' to suppressed; an expiry is required.");
        }

        if (!WorkflowTransitions.IsAllowed(WorkflowStatus, target))
        {
            return DomainErrors.InvalidTransition(Id.Value, WorkflowStatus.ToWire(), target.ToWire());
        }

        if (WorkflowStatus == target)
        {
            return IssueChange.None;
        }

        var changes = new List<AuditChange>
        {
            new(nameof(WorkflowStatus), WorkflowStatus.ToWire(), target.ToWire())
        };

        if (SuppressionExpiresAtUtc is not null)
        {
            changes.Add(new AuditChange(nameof(SuppressionExpiresAtUtc), Format(SuppressionExpiresAtUtc), null));
            SuppressionExpiresAtUtc = null;
        }

        WorkflowStatus = target;
        return Apply(AuditEventKind.IssueWorkflowStatusChanged, changes, now);
    }

    public Result<IssueChange> Suppress(DateTimeOffset expiresAtUtc, DateTimeOffset now)
    {
        if (expiresAtUtc <= now)
        {
            return DomainErrors.SuppressionExpiryInPast(Id.Value);
        }

        if (!WorkflowTransitions.IsAllowed(WorkflowStatus, WorkflowStatus.Suppressed))
        {
            return DomainErrors.InvalidTransition(
                Id.Value, WorkflowStatus.ToWire(), WorkflowStatus.Suppressed.ToWire());
        }

        var changes = new List<AuditChange>();
        if (WorkflowStatus != WorkflowStatus.Suppressed)
        {
            changes.Add(new AuditChange(
                nameof(WorkflowStatus), WorkflowStatus.ToWire(), WorkflowStatus.Suppressed.ToWire()));
            WorkflowStatus = WorkflowStatus.Suppressed;
        }

        if (SuppressionExpiresAtUtc != expiresAtUtc)
        {
            changes.Add(new AuditChange(
                nameof(SuppressionExpiresAtUtc), Format(SuppressionExpiresAtUtc), Format(expiresAtUtc)));
            SuppressionExpiresAtUtc = expiresAtUtc;
        }

        return Apply(AuditEventKind.IssueSuppressed, changes, now);
    }

    public Result<IssueChange> Unsuppress(DateTimeOffset now)
    {
        if (WorkflowStatus != WorkflowStatus.Suppressed)
        {
            return DomainErrors.NotSuppressed(Id.Value);
        }

        return ReleaseSuppression(AuditEventKind.IssueWorkflowStatusChanged, now);
    }

    /// <summary>Driven by the scheduled expiry sweep rather than by a human.</summary>
    public IssueChange ExpireSuppression(DateTimeOffset now)
    {
        if (!IsSuppressionExpired(now))
        {
            return IssueChange.None;
        }

        return ReleaseSuppression(AuditEventKind.IssueSuppressionExpired, now);
    }

    private IssueChange ReleaseSuppression(AuditEventKind kind, DateTimeOffset now)
    {
        var changes = new List<AuditChange>
        {
            new(nameof(WorkflowStatus), WorkflowStatus.ToWire(), WorkflowStatus.Todo.ToWire())
        };

        if (SuppressionExpiresAtUtc is not null)
        {
            changes.Add(new AuditChange(nameof(SuppressionExpiresAtUtc), Format(SuppressionExpiresAtUtc), null));
            SuppressionExpiresAtUtc = null;
        }

        WorkflowStatus = WorkflowStatus.Todo;
        return Apply(kind, changes, now);
    }

    private IssueChange Apply(AuditEventKind kind, IReadOnlyList<AuditChange> changes, DateTimeOffset now)
    {
        if (changes.Count == 0)
        {
            return IssueChange.None;
        }

        UpdatedAtUtc = now;
        Version++;
        return new IssueChange(kind, changes);
    }

    private static string? Format(DateTimeOffset? value) => value?.ToString("O");
}
