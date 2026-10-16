using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Issues;

/// <summary>
/// The rules engine's write path. Call it as many times as you like within a run; it is
/// idempotent, so a rule that re-emits the same detection produces no second issue and no
/// second audit entry. The group is taken from the run, so a rule cannot write into another
/// group's issues.
/// </summary>
public sealed record UpsertDetectionsCommand(
    string RunId,
    IReadOnlyCollection<IssueDetection> Detections,
    Actor Actor) : ICommand<DetectionUpsertResult>;

internal sealed class UpsertDetectionsCommandHandler(
    IRuleRunStore runs,
    IIssueStore issues,
    IAuditStore audit,
    TimeProvider clock,
    CleanseCoreOptions options)
    : ICommandHandler<UpsertDetectionsCommand, DetectionUpsertResult>
{
    private static readonly IssueChange CreationChange = new(
        AuditEventKind.IssueCreated,
        [
            new AuditChange(nameof(Issue.Status), null, StatusNames.Active),
            new AuditChange(nameof(Issue.WorkflowStatus), null, StatusNames.Todo)
        ]);

    public async Task<Result<DetectionUpsertResult>> HandleAsync(
        UpsertDetectionsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Detections);
        ArgumentNullException.ThrowIfNull(command.Actor);

        if (string.IsNullOrWhiteSpace(command.RunId))
        {
            return DomainErrors.Validation("A run id is required.");
        }

        var run = await runs.GetAsync(command.RunId.Trim(), cancellationToken);
        if (run is null)
        {
            return DomainErrors.RunNotFound(command.RunId);
        }

        if (!run.IsRunning)
        {
            return DomainErrors.RunNotRunning(run.RunId, run.State.ToString().ToLowerInvariant());
        }

        // Last one wins: a rule emitting the same key twice in one call must not create two issues.
        var deduplicated = new Dictionary<string, IssueDetection>(command.Detections.Count, StringComparer.Ordinal);
        foreach (var detection in command.Detections)
        {
            ArgumentNullException.ThrowIfNull(detection);
            deduplicated[detection.Id.Value] = detection;
        }

        if (deduplicated.Count == 0)
        {
            return DetectionUpsertResult.Empty;
        }

        var now = clock.GetUtcNow();
        var total = DetectionUpsertResult.Empty;

        foreach (var chunk in deduplicated.Values.Chunk(options.DetectionBatchSize))
        {
            var chunkResult = await UpsertChunkAsync(run, chunk, command.Actor, now, cancellationToken);
            if (chunkResult.IsFailure)
            {
                return chunkResult;
            }

            total = total.Add(chunkResult.Value);
        }

        var recorded = run.RecordUpserts(total.ToCounters());
        if (recorded.IsFailure)
        {
            return recorded.ToFailure<DetectionUpsertResult>();
        }

        await runs.SaveAsync(run, cancellationToken);
        return total;
    }

    private async Task<Result<DetectionUpsertResult>> UpsertChunkAsync(
        RuleRun run,
        IReadOnlyList<IssueDetection> chunk,
        Actor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = (await issues.GetManyAsync([.. chunk.Select(d => d.Id)], cancellationToken))
            .ToDictionary(issue => issue.Id.Value, StringComparer.Ordinal);

        var toSave = new List<Issue>(chunk.Count);
        var entries = new List<AuditEntry>();
        int created = 0, reactivated = 0, updated = 0, unchanged = 0;

        foreach (var detection in chunk)
        {
            if (!existing.TryGetValue(detection.Id.Value, out var issue))
            {
                issue = Issue.FromDetection(detection, run.GroupId, run.RunId, now);
                created++;
                entries.Add(AuditEntry.ForIssue(issue.Id, CreationChange, actor, now, run.RunId));
                toSave.Add(issue);
                continue;
            }

            if (!string.Equals(issue.GroupId, run.GroupId, StringComparison.Ordinal))
            {
                return DomainErrors.IssueGroupMismatch(issue.Id.Value, run.GroupId, issue.GroupId);
            }

            var change = issue.RecordDetection(run.RunId, detection.Context, now);
            if (change.Kind == AuditEventKind.IssueReactivated)
            {
                reactivated++;
            }
            else if (change.IsMaterial)
            {
                updated++;
            }
            else
            {
                unchanged++;
            }

            if (change.IsMaterial)
            {
                entries.Add(AuditEntry.ForIssue(issue.Id, change, actor, now, run.RunId));
            }

            // Saved even when nothing material changed: the advanced run id is the only thing
            // keeping the end-of-run sweep from deactivating this issue.
            toSave.Add(issue);
        }

        await issues.SaveBatchAsync(toSave, cancellationToken);
        if (entries.Count > 0)
        {
            await audit.AppendBatchAsync(entries, cancellationToken);
        }

        return new DetectionUpsertResult(chunk.Count, created, reactivated, updated, unchanged);
    }
}
