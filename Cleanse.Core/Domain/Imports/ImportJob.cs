namespace Cleanse.Core.Domain.Imports;

public enum ImportJobStatus
{
    Pending,
    Processing,
    Completed,
    Failed,

    /// <summary>Its worker stopped reporting. Reclaimed by the reaper, not by the worker itself.</summary>
    Abandoned
}

public sealed record ImportJobCounters(
    int TotalRows,
    int ProcessedRows,
    int Matched,
    int Unmatched,
    int DuplicatesCollapsed,
    int Invalid)
{
    public static readonly ImportJobCounters Empty = new(0, 0, 0, 0, 0, 0);

    /// <summary>Zero rather than a divide-by-zero when the row count is not yet known.</summary>
    public int PercentComplete => TotalRows <= 0
        ? 0
        : (int)Math.Clamp(Math.Round(ProcessedRows / (double)TotalRows * 100), 0, 100);
}

public sealed record ImportJobSnapshot(
    Guid Id,
    string GroupId,
    string FileName,
    string BlobKey,
    ImportJobStatus Status,
    ImportJobCounters Counters,
    string RequestedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset? HeartbeatAtUtc,
    string? FailureReason,
    int Version);

/// <summary>
/// One upload of an external extract. Tracks progress so a long import can be watched from the
/// front end, and carries a heartbeat so a worker killed mid-run leaves evidence rather than a
/// job that looks alive forever.
/// </summary>
public sealed class ImportJob
{
    private ImportJob(
        Guid id,
        string groupId,
        string fileName,
        string blobKey,
        ImportJobStatus status,
        ImportJobCounters counters,
        string requestedBy,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? finishedAtUtc,
        DateTimeOffset? heartbeatAtUtc,
        string? failureReason,
        int version)
    {
        Id = id;
        GroupId = groupId;
        FileName = fileName;
        BlobKey = blobKey;
        Status = status;
        Counters = counters;
        RequestedBy = requestedBy;
        CreatedAtUtc = createdAtUtc;
        StartedAtUtc = startedAtUtc;
        FinishedAtUtc = finishedAtUtc;
        HeartbeatAtUtc = heartbeatAtUtc;
        FailureReason = failureReason;
        Version = version;
    }

    public Guid Id { get; }

    public string GroupId { get; }

    public string FileName { get; }

    public string BlobKey { get; }

    public ImportJobStatus Status { get; private set; }

    public ImportJobCounters Counters { get; private set; }

    public string RequestedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public DateTimeOffset? HeartbeatAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public int Version { get; private set; }

    public bool IsTerminal => Status is ImportJobStatus.Completed or ImportJobStatus.Failed
        or ImportJobStatus.Abandoned;

    public static ImportJob Create(
        Guid id,
        string groupId,
        string fileName,
        string blobKey,
        string requestedBy,
        int totalRows,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRows);

        return new ImportJob(
            id,
            groupId.Trim(),
            fileName.Trim(),
            blobKey.Trim(),
            ImportJobStatus.Pending,
            ImportJobCounters.Empty with { TotalRows = totalRows },
            requestedBy.Trim(),
            now,
            startedAtUtc: null,
            finishedAtUtc: null,
            heartbeatAtUtc: null,
            failureReason: null,
            version: 1);
    }

    public static ImportJob FromSnapshot(ImportJobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ImportJob(
            snapshot.Id,
            snapshot.GroupId,
            snapshot.FileName,
            snapshot.BlobKey,
            snapshot.Status,
            snapshot.Counters,
            snapshot.RequestedBy,
            snapshot.CreatedAtUtc,
            snapshot.StartedAtUtc,
            snapshot.FinishedAtUtc,
            snapshot.HeartbeatAtUtc,
            snapshot.FailureReason,
            snapshot.Version);
    }

    public ImportJobSnapshot ToSnapshot() => new(
        Id, GroupId, FileName, BlobKey, Status, Counters, RequestedBy,
        CreatedAtUtc, StartedAtUtc, FinishedAtUtc, HeartbeatAtUtc, FailureReason, Version);

    public Result<ImportJob> Start(DateTimeOffset now)
    {
        if (Status != ImportJobStatus.Pending)
        {
            return DomainErrors.ImportNotPending(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Processing;
        StartedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    public Result<ImportJob> RecordProgress(ImportJobCounters delta, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (Status != ImportJobStatus.Processing)
        {
            return DomainErrors.ImportNotProcessing(Id, Status.ToString().ToLowerInvariant());
        }

        Counters = Counters with
        {
            ProcessedRows = Counters.ProcessedRows + delta.ProcessedRows,
            Matched = Counters.Matched + delta.Matched,
            Unmatched = Counters.Unmatched + delta.Unmatched,
            DuplicatesCollapsed = Counters.DuplicatesCollapsed + delta.DuplicatesCollapsed,
            Invalid = Counters.Invalid + delta.Invalid
        };

        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    public Result<ImportJob> Complete(DateTimeOffset now)
    {
        if (Status != ImportJobStatus.Processing)
        {
            return DomainErrors.ImportNotProcessing(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Completed;
        FinishedAtUtc = now;
        HeartbeatAtUtc = now;
        Version++;
        return this;
    }

    public Result<ImportJob> Fail(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return DomainErrors.ImportAlreadyFinished(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        FinishedAtUtc = now;
        Version++;
        return this;
    }

    /// <summary>Reclaims a job whose worker stopped reporting. Only the reaper calls this.</summary>
    public Result<ImportJob> Abandon(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return DomainErrors.ImportAlreadyFinished(Id, Status.ToString().ToLowerInvariant());
        }

        Status = ImportJobStatus.Abandoned;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        FinishedAtUtc = now;
        Version++;
        return this;
    }

    public bool HasStalled(DateTimeOffset now, TimeSpan threshold) =>
        Status == ImportJobStatus.Processing &&
        HeartbeatAtUtc is { } beat &&
        now - beat > threshold;
}

/// <summary>A row the import could not apply, kept for the reconciliation report.</summary>
public sealed record ImportJobRejection(
    Guid ImportJobId,
    long RowNumber,
    string? IssueId,
    string Reason,
    string Detail);

public static class ImportRejectionReasons
{
    public const string IssueNotFound = "issue_not_found";
    public const string InvalidRow = "invalid_row";
}
