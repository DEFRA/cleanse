using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Contracts;

internal static class DtoMappings
{
    public static IssueDto ToDto(this Issue issue) => new(
        issue.Id.Value,
        issue.GroupId,
        issue.RuleId,
        issue.RunId,
        issue.Status.ToWire(),
        issue.WorkflowStatus.ToWire(),
        issue.AssignedTo,
        issue.Notes,
        issue.ExternalNotes,
        issue.Priority?.ToWire(),
        issue.ExternalReceivedAtUtc,
        issue.SuppressionExpiresAtUtc,
        issue.Context,
        issue.CreatedAtUtc,
        issue.UpdatedAtUtc,
        issue.ActivatedAtUtc,
        issue.DeactivatedAtUtc,
        issue.Version);

    public static RuleRunDto ToDto(this RuleRun run, DateTimeOffset now) => new(
        run.RunId,
        run.GroupId,
        run.State.ToString().ToLowerInvariant(),
        run.StartedAtUtc,
        run.FinishedAtUtc,
        run.AbandonReason,
        run.Counters.DetectionsProcessed,
        run.Counters.IssuesCreated,
        run.Counters.IssuesReactivated,
        run.Counters.IssuesUpdated,
        run.Counters.IssuesUnchanged,
        run.Counters.IssuesDeactivated,
        run.Progress.CurrentStage,
        run.Progress.RecordsProcessed,
        run.Progress.RecordsTotal,
        run.ProgressPercent,
        run.Elapsed(now).TotalSeconds,
        run.EstimatedCompletionAtUtc(now),
        run.HeartbeatAtUtc,
        run.CancellationRequested,
        run.Failure?.Message,
        run.Failure?.Detail);

    public static RuleRunSummaryDto ToSummaryDto(this RuleRun run, DateTimeOffset now) => new(
        run.RunId,
        run.GroupId,
        run.State.ToString().ToLowerInvariant(),
        run.StartedAtUtc,
        run.FinishedAtUtc,
        run.AbandonReason,
        run.Counters.DetectionsProcessed,
        run.Counters.IssuesCreated,
        run.Counters.IssuesReactivated,
        run.Counters.IssuesUpdated,
        run.Counters.IssuesUnchanged,
        run.Counters.IssuesDeactivated,
        run.Progress.CurrentStage,
        run.Progress.RecordsProcessed,
        run.Progress.RecordsTotal,
        run.ProgressPercent,
        run.Elapsed(now).TotalSeconds,
        run.EstimatedCompletionAtUtc(now),
        run.HeartbeatAtUtc,
        run.CancellationRequested,
        run.Failure?.Message);

    public static AuditEntryDto ToDto(this AuditEntry entry) => new(
        entry.Id,
        entry.IssueId?.Value,
        entry.RunId,
        entry.Kind.ToString(),
        entry.Actor.Id,
        entry.Actor.DisplayName,
        entry.Actor.Kind.ToString().ToLowerInvariant(),
        entry.AtUtc,
        [.. entry.Changes.Select(c => new AuditChangeDto(c.Field, c.From, c.To))]);

    public static IssueHistoryEntryDto ToDto(this IssueHistoryEntry entry) => new(
        entry.Id,
        entry.IssueId.Value,
        entry.ActionType.ToString(),
        entry.Comments,
        entry.CreatedAtUtc,
        entry.CreatedBy.Id,
        entry.CreatedBy.DisplayName);

    public static IssueStatusCountDto ToDto(this IssueStatusCount count) =>
        new(count.Status.ToWire(), count.WorkflowStatus.ToWire(), count.Count);

    public static PagedResult<TDto> MapToDto<TSource, TDto>(
        this PagedResult<TSource> source,
        Func<TSource, TDto> selector) => source.Map(selector);

    public static ImportJobDto ToDto(this ImportJob job) => new(
        job.Id,
        job.GroupId,
        job.FileName,
        job.Status.ToString().ToLowerInvariant(),
        job.Counters.TotalRows,
        job.Counters.ProcessedRows,
        job.Counters.PercentComplete,
        job.Counters.Matched,
        job.Counters.Unmatched,
        job.Counters.DuplicatesCollapsed,
        job.Counters.Invalid,
        job.RequestedBy,
        job.CreatedAtUtc,
        job.StartedAtUtc,
        job.FinishedAtUtc,
        job.FailureReason);
}
