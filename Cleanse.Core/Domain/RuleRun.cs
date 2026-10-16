namespace Cleanse.Core.Domain;

public enum RunState
{
    Running,
    Completed,
    Abandoned,
    Failed,
    Cancelled
}

public sealed record RuleRunCounters(
    int DetectionsProcessed,
    int IssuesCreated,
    int IssuesReactivated,
    int IssuesUpdated,
    int IssuesUnchanged,
    int IssuesDeactivated)
{
    public static readonly RuleRunCounters Empty = new(0, 0, 0, 0, 0, 0);

    public RuleRunCounters Add(RuleRunCounters other) => new(
        DetectionsProcessed + other.DetectionsProcessed,
        IssuesCreated + other.IssuesCreated,
        IssuesReactivated + other.IssuesReactivated,
        IssuesUpdated + other.IssuesUpdated,
        IssuesUnchanged + other.IssuesUnchanged,
        IssuesDeactivated + other.IssuesDeactivated);
}

/// <summary>
/// Where a run has got to. <see cref="RecordsTotal"/> is zero until the engine has counted its
/// work, which is why the derived percentage guards against it rather than assuming it is set.
/// </summary>
public sealed record RuleRunProgress(string? CurrentStage, int RecordsProcessed, int RecordsTotal)
{
    public static readonly RuleRunProgress None = new(null, 0, 0);
}

/// <summary>Why a run failed. <see cref="Detail"/> carries the full technical dump.</summary>
public sealed record RuleRunFailure(string Message, string? Detail);

public sealed record RuleRunSnapshot(
    string RunId,
    string GroupId,
    RunState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? AbandonReason,
    RuleRunCounters Counters,
    RuleRunProgress Progress,
    DateTimeOffset? HeartbeatAtUtc,
    bool CancellationRequested,
    RuleRunFailure? Failure,
    int Version);

/// <summary>
/// One overnight pass of a rule group. A run is always scoped to exactly one group, which is
/// what makes the end-of-run sweep safe to apply group-wide.
/// </summary>
public sealed class RuleRun
{
    private RuleRun(
        string runId,
        string groupId,
        RunState state,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? finishedAtUtc,
        string? abandonReason,
        RuleRunCounters counters,
        RuleRunProgress progress,
        DateTimeOffset? heartbeatAtUtc,
        bool cancellationRequested,
        RuleRunFailure? failure,
        int version)
    {
        RunId = runId;
        GroupId = groupId;
        State = state;
        StartedAtUtc = startedAtUtc;
        FinishedAtUtc = finishedAtUtc;
        AbandonReason = abandonReason;
        Counters = counters;
        Progress = progress;
        HeartbeatAtUtc = heartbeatAtUtc;
        CancellationRequested = cancellationRequested;
        Failure = failure;
        Version = version;
    }

    public string RunId { get; }

    public string GroupId { get; }

    public RunState State { get; private set; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>Why the run ended without sweeping. Set by both abandon and cancel.</summary>
    public string? AbandonReason { get; private set; }

    public RuleRunCounters Counters { get; private set; }

    public RuleRunProgress Progress { get; private set; }

    /// <summary>Last sign of life. A run whose heartbeat has gone quiet is reaped as abandoned.</summary>
    public DateTimeOffset? HeartbeatAtUtc { get; private set; }

    public bool CancellationRequested { get; private set; }

    public RuleRunFailure? Failure { get; private set; }

    public int Version { get; private set; }

    public bool IsRunning => State == RunState.Running;

    public double ProgressPercent => Progress.RecordsTotal <= 0
        ? State == RunState.Completed ? 100d : 0d
        : Math.Clamp(Math.Round(Progress.RecordsProcessed * 100d / Progress.RecordsTotal, 2), 0d, 100d);

    /// <summary>Wall-clock time so far, or the final duration once the run has ended.</summary>
    public TimeSpan Elapsed(DateTimeOffset at)
    {
        var end = FinishedAtUtc ?? at;
        return end > StartedAtUtc ? end - StartedAtUtc : TimeSpan.Zero;
    }

    /// <summary>
    /// When the run is projected to finish, extrapolated from the rate achieved so far. Null
    /// until there is enough of a sample to extrapolate from.
    /// </summary>
    public DateTimeOffset? EstimatedCompletionAtUtc(DateTimeOffset at)
    {
        if (!IsRunning)
        {
            return FinishedAtUtc;
        }

        if (Progress.RecordsProcessed <= 0 ||
            Progress.RecordsTotal <= 0 ||
            Progress.RecordsProcessed >= Progress.RecordsTotal)
        {
            return null;
        }

        var elapsed = at - StartedAtUtc;
        if (elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var projected = elapsed * (Progress.RecordsTotal / (double)Progress.RecordsProcessed);
        return StartedAtUtc + projected;
    }

    public static RuleRun Start(string runId, string groupId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        return new RuleRun(
            runId.Trim(),
            groupId.Trim(),
            RunState.Running,
            now,
            finishedAtUtc: null,
            abandonReason: null,
            RuleRunCounters.Empty,
            RuleRunProgress.None,
            heartbeatAtUtc: now,
            cancellationRequested: false,
            failure: null,
            version: 1);
    }

    public static RuleRun FromSnapshot(RuleRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new RuleRun(
            snapshot.RunId,
            snapshot.GroupId,
            snapshot.State,
            snapshot.StartedAtUtc,
            snapshot.FinishedAtUtc,
            snapshot.AbandonReason,
            snapshot.Counters,
            snapshot.Progress,
            snapshot.HeartbeatAtUtc,
            snapshot.CancellationRequested,
            snapshot.Failure,
            snapshot.Version);
    }

    public RuleRunSnapshot ToSnapshot() => new(
        RunId,
        GroupId,
        State,
        StartedAtUtc,
        FinishedAtUtc,
        AbandonReason,
        Counters,
        Progress,
        HeartbeatAtUtc,
        CancellationRequested,
        Failure,
        Version);

    public Result<RuleRun> RecordUpserts(RuleRunCounters counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        if (!IsRunning)
        {
            return DomainErrors.RunNotRunning(RunId, State.ToString().ToLowerInvariant());
        }

        Counters = Counters.Add(counters);
        Version++;
        return this;
    }

    /// <summary>
    /// Records where the engine has got to and refreshes the heartbeat. Called once per flushed
    /// batch rather than once per record, so a run costs tens of writes rather than tens of thousands.
    /// </summary>
    public Result<RuleRun> ReportProgress(string? currentStage, int recordsProcessed, int recordsTotal, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return DomainErrors.RunNotRunning(RunId, State.ToString().ToLowerInvariant());
        }

        ArgumentOutOfRangeException.ThrowIfNegative(recordsProcessed);
        ArgumentOutOfRangeException.ThrowIfNegative(recordsTotal);

        Progress = new RuleRunProgress(
            string.IsNullOrWhiteSpace(currentStage) ? Progress.CurrentStage : currentStage.Trim(),
            recordsProcessed,
            recordsTotal);
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    /// <summary>
    /// Flags the run for cancellation. The engine polls this between batches, so the run stays
    /// running until it reaches a point where stopping leaves the data consistent.
    /// </summary>
    public Result<RuleRun> RequestCancellation(DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return DomainErrors.RunNotRunning(RunId, State.ToString().ToLowerInvariant());
        }

        if (CancellationRequested)
        {
            return this;
        }

        CancellationRequested = true;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    public Result<RuleRun> Complete(int issuesDeactivated, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return DomainErrors.RunNotRunning(RunId, State.ToString().ToLowerInvariant());
        }

        Counters = Counters.Add(RuleRunCounters.Empty with { IssuesDeactivated = issuesDeactivated });
        State = RunState.Completed;

        // A run that reached the end has no stage it is "currently" in, and the last one is
        // always the sweep, so keeping it would report a stage that means nothing. The runs
        // that stop early keep theirs, because where they stopped is the diagnostic.
        Progress = Progress with { CurrentStage = null };

        FinishedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    /// <summary>
    /// Ends a run without sweeping. Used when a rule crashed part way through: a group-wide
    /// sweep after a partial run would wrongly deactivate every issue the missing rules own.
    /// </summary>
    public Result<RuleRun> Abandon(string reason, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return DomainErrors.RunNotRunning(RunId, State.ToString().ToLowerInvariant());
        }

        State = RunState.Abandoned;
        AbandonReason = Clean(reason);
        FinishedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    /// <summary>Ends a run that a caller asked to stop. Like abandon, no sweep is performed.</summary>
    public Result<RuleRun> Cancel(string? reason, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return DomainErrors.RunNotRunning(RunId, State.ToString().ToLowerInvariant());
        }

        State = RunState.Cancelled;
        CancellationRequested = true;
        AbandonReason = Clean(reason) ?? "Cancelled.";
        FinishedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    /// <summary>
    /// Ends a run that threw. No sweep, and the full technical detail is kept so the failure can
    /// be diagnosed without correlating against application logs.
    /// </summary>
    public Result<RuleRun> Fail(string message, string? detail, DateTimeOffset now)
    {
        if (!IsRunning)
        {
            return DomainErrors.RunNotRunning(RunId, State.ToString().ToLowerInvariant());
        }

        State = RunState.Failed;
        Failure = new RuleRunFailure(Clean(message) ?? "The run failed.", Clean(detail));
        FinishedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
