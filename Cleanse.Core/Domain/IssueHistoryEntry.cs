namespace Cleanse.Core.Domain;

public enum HistoryActionType
{
    Comment,
    LetterSent,
    PhoneCall
}

public sealed record IssueHistorySnapshot(
    Guid Id,
    string IssueId,
    HistoryActionType ActionType,
    string Comments,
    DateTimeOffset CreatedAtUtc,
    Actor CreatedBy);

/// <summary>
/// A human case action against an issue. Distinct from the audit trail, which records
/// system-generated state changes rather than case work.
/// </summary>
public sealed class IssueHistoryEntry
{
    private IssueHistoryEntry(
        Guid id,
        IssueId issueId,
        HistoryActionType actionType,
        string comments,
        DateTimeOffset createdAtUtc,
        Actor createdBy)
    {
        Id = id;
        IssueId = issueId;
        ActionType = actionType;
        Comments = comments;
        CreatedAtUtc = createdAtUtc;
        CreatedBy = createdBy;
    }

    public Guid Id { get; }

    public IssueId IssueId { get; }

    public HistoryActionType ActionType { get; }

    public string Comments { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public Actor CreatedBy { get; }

    public static IssueHistoryEntry Create(
        IssueId issueId,
        HistoryActionType actionType,
        string comments,
        Actor createdBy,
        DateTimeOffset now)
    {
        // Version 7 so ids sort by creation time on any store that indexes them.
        return CreateWithId(Guid.CreateVersion7(now), issueId, actionType, comments, createdBy, now);
    }

    /// <summary>
    /// For entries mirrored from an external system, where the id is derived from the source row
    /// so that re-importing upserts rather than duplicating.
    /// </summary>
    public static IssueHistoryEntry CreateWithId(
        Guid id,
        IssueId issueId,
        HistoryActionType actionType,
        string comments,
        Actor createdBy,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comments);
        ArgumentNullException.ThrowIfNull(createdBy);

        return new IssueHistoryEntry(id, issueId, actionType, comments.Trim(), now, createdBy);
    }

    public static IssueHistoryEntry FromSnapshot(IssueHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new IssueHistoryEntry(
            snapshot.Id,
            IssueId.From(snapshot.IssueId),
            snapshot.ActionType,
            snapshot.Comments,
            snapshot.CreatedAtUtc,
            snapshot.CreatedBy);
    }

    public IssueHistorySnapshot ToSnapshot() =>
        new(Id, IssueId.Value, ActionType, Comments, CreatedAtUtc, CreatedBy);
}
