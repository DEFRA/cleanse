using System.Text.Json;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// Translation between physical rows and domain snapshots. Statuses move through the same wire
/// names the DTOs expose, so what is in the column is what an API response shows.
/// </summary>
internal static class RowMappings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly string EmptyContextJson = "{}";

    public static IssueRow ToRow(this IssueSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        GroupId = snapshot.GroupId,
        RuleId = snapshot.RuleId,
        RunId = snapshot.RunId,
        Status = snapshot.Status.ToWire(),
        WorkflowStatus = snapshot.WorkflowStatus.ToWire(),
        AssignedTo = snapshot.AssignedTo,
        Notes = snapshot.Notes,
        ExternalNotes = snapshot.ExternalNotes,
        Priority = snapshot.Priority?.ToWire(),
        ExternalReceivedAtUtc = snapshot.ExternalReceivedAtUtc,
        SuppressionExpiresAtUtc = snapshot.SuppressionExpiresAtUtc,
        Context = SerialiseContext(snapshot.Context),
        CreatedAtUtc = snapshot.CreatedAtUtc,
        UpdatedAtUtc = snapshot.UpdatedAtUtc,
        ActivatedAtUtc = snapshot.ActivatedAtUtc,
        DeactivatedAtUtc = snapshot.DeactivatedAtUtc,
        Version = snapshot.Version
    };

    public static Issue ToDomain(this IssueRow row) => Issue.FromSnapshot(new IssueSnapshot(
        row.Id,
        row.GroupId,
        row.RuleId,
        row.RunId,
        ParseIssueStatus(row.Status),
        ParseWorkflowStatus(row.WorkflowStatus),
        row.AssignedTo,
        row.Notes,
        row.ExternalNotes,
        row.Priority is null ? null : ParsePriority(row.Priority),
        row.ExternalReceivedAtUtc,
        row.SuppressionExpiresAtUtc,
        DeserialiseContext(row.Context),
        row.CreatedAtUtc,
        row.UpdatedAtUtc,
        row.ActivatedAtUtc,
        row.DeactivatedAtUtc,
        row.Version));

    public static IssuePriority ParsePriority(string value) =>
        StatusNames.TryParsePriority(value, out var priority)
            ? priority
            : throw new InvalidOperationException($"'{value}' is not a known priority.");

    public static string SerialiseContext(IssueContext context) =>
        context.Count == 0
            ? EmptyContextJson
            : JsonSerializer.Serialize(context.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), Json);

    public static IssueContext DeserialiseContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return IssueContext.Empty;
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json);
        return IssueContext.From(values);
    }

    public static RuleRunRow ToRow(this RuleRunSnapshot snapshot) => new()
    {
        RunId = snapshot.RunId,
        GroupId = snapshot.GroupId,
        State = snapshot.State.ToString().ToLowerInvariant(),
        StartedAtUtc = snapshot.StartedAtUtc,
        FinishedAtUtc = snapshot.FinishedAtUtc,
        AbandonReason = snapshot.AbandonReason,
        DetectionsProcessed = snapshot.Counters.DetectionsProcessed,
        IssuesCreated = snapshot.Counters.IssuesCreated,
        IssuesReactivated = snapshot.Counters.IssuesReactivated,
        IssuesUpdated = snapshot.Counters.IssuesUpdated,
        IssuesUnchanged = snapshot.Counters.IssuesUnchanged,
        IssuesDeactivated = snapshot.Counters.IssuesDeactivated,
        CurrentStage = snapshot.Progress.CurrentStage,
        RecordsProcessed = snapshot.Progress.RecordsProcessed,
        RecordsTotal = snapshot.Progress.RecordsTotal,
        HeartbeatAtUtc = snapshot.HeartbeatAtUtc,
        CancellationRequested = snapshot.CancellationRequested,
        FailureMessage = snapshot.Failure?.Message,
        FailureDetail = snapshot.Failure?.Detail,
        Version = snapshot.Version
    };

    public static RuleRun ToDomain(this RuleRunRow row) => RuleRun.FromSnapshot(new RuleRunSnapshot(
        row.RunId,
        row.GroupId,
        Enum.Parse<RunState>(row.State, ignoreCase: true),
        row.StartedAtUtc,
        row.FinishedAtUtc,
        row.AbandonReason,
        new RuleRunCounters(
            row.DetectionsProcessed,
            row.IssuesCreated,
            row.IssuesReactivated,
            row.IssuesUpdated,
            row.IssuesUnchanged,
            row.IssuesDeactivated),
        new RuleRunProgress(row.CurrentStage, row.RecordsProcessed, row.RecordsTotal),
        row.HeartbeatAtUtc,
        row.CancellationRequested,
        row.FailureMessage is null ? null : new RuleRunFailure(row.FailureMessage, row.FailureDetail),
        row.Version));

    public static AuditEntryRow ToRow(this AuditEntry entry) => new()
    {
        Id = entry.Id,
        IssueId = entry.IssueId?.Value,
        RunId = entry.RunId,
        Kind = entry.Kind.ToString(),
        ActorId = entry.Actor.Id,
        ActorDisplayName = entry.Actor.DisplayName,
        ActorKind = entry.Actor.Kind.ToString(),
        AtUtc = entry.AtUtc,
        Changes = JsonSerializer.Serialize(entry.Changes, Json)
    };

    public static AuditEntry ToDomain(this AuditEntryRow row) => AuditEntry.Rehydrate(
        row.Id,
        row.IssueId is null ? null : IssueId.From(row.IssueId),
        row.RunId,
        Enum.Parse<AuditEventKind>(row.Kind),
        RebuildActor(row.ActorId, row.ActorDisplayName, row.ActorKind),
        row.AtUtc,
        JsonSerializer.Deserialize<List<AuditChange>>(row.Changes, Json) ?? []);

    public static IssueHistoryRow ToRow(this IssueHistorySnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        IssueId = snapshot.IssueId,
        ActionType = snapshot.ActionType.ToString(),
        Comments = snapshot.Comments,
        CreatedAtUtc = snapshot.CreatedAtUtc,
        CreatedById = snapshot.CreatedBy.Id,
        CreatedByDisplayName = snapshot.CreatedBy.DisplayName,
        CreatedByKind = snapshot.CreatedBy.Kind.ToString()
    };

    public static IssueHistoryEntry ToDomain(this IssueHistoryRow row) =>
        IssueHistoryEntry.FromSnapshot(new IssueHistorySnapshot(
            row.Id,
            row.IssueId,
            Enum.Parse<HistoryActionType>(row.ActionType),
            row.Comments,
            row.CreatedAtUtc,
            RebuildActor(row.CreatedById, row.CreatedByDisplayName, row.CreatedByKind)));

    private static Actor RebuildActor(string id, string displayName, string kind) =>
        Enum.Parse<ActorKind>(kind, ignoreCase: true) == ActorKind.System
            ? id == Actor.SystemId ? Actor.System : Actor.Machine(id, displayName)
            : Actor.Human(id, displayName);

    public static IssueStatus ParseIssueStatus(string value) =>
        StatusNames.TryParseIssueStatus(value, out var status)
            ? status
            : throw new InvalidOperationException($"'{value}' is not a known issue status.");

    public static WorkflowStatus ParseWorkflowStatus(string value) =>
        StatusNames.TryParseWorkflowStatus(value, out var status)
            ? status
            : throw new InvalidOperationException($"'{value}' is not a known workflow status.");
}
