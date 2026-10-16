using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Commands.Imports;

/// <summary>
/// Applies one chunk of an external extract. Rows with no matching issue are recorded for the
/// reconciliation report rather than creating anything: the rules engine owns issue creation.
/// </summary>
public sealed record ApplyImportBatchCommand(
    Guid ImportJobId,
    IReadOnlyCollection<ExternalIssueRow> Rows,
    Actor Actor) : ICommand<ImportBatchResult>;

internal sealed class ApplyImportBatchCommandHandler(
    IImportJobStore imports,
    IIssueStore issues,
    IIssueHistoryStore history,
    IAuditStore audit,
    TimeProvider clock)
    : ICommandHandler<ApplyImportBatchCommand, ImportBatchResult>
{
    private const string Source = "spol";

    public async Task<Result<ImportBatchResult>> HandleAsync(
        ApplyImportBatchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Rows);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var job = await imports.GetAsync(command.ImportJobId, cancellationToken);
        if (job is null)
        {
            return DomainErrors.ImportNotFound(command.ImportJobId);
        }

        if (job.Status != ImportJobStatus.Processing)
        {
            return DomainErrors.ImportNotProcessing(job.Id, job.Status.ToString().ToLowerInvariant());
        }

        if (command.Rows.Count == 0)
        {
            return ImportBatchResult.Empty;
        }

        var now = clock.GetUtcNow();
        var rows = command.Rows.ToList();

        var existing = (await issues.GetManyAsync([.. rows.Select(r => r.IssueId)], cancellationToken))
            .ToDictionary(issue => issue.Id.Value, StringComparer.Ordinal);

        var toSave = new List<Issue>(rows.Count);
        var auditEntries = new List<AuditEntry>();
        var historyEntries = new List<IssueHistoryEntry>();
        var rejections = new List<ImportJobRejection>();

        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.IssueId.Value, out var issue))
            {
                rejections.Add(new ImportJobRejection(
                    job.Id,
                    row.RowNumber,
                    row.IssueId.Value,
                    ImportRejectionReasons.IssueNotFound,
                    $"No issue exists for '{row.IssueId.Value}'."));
                continue;
            }

            var change = issue.ApplyExternalState(row.ToState(), now);
            if (change.IsMaterial)
            {
                auditEntries.Add(AuditEntry.ForIssue(issue.Id, change, command.Actor, now, job.Id.ToString()));
                toSave.Add(issue);
            }

            foreach (var contact in row.Contacts)
            {
                historyEntries.Add(IssueHistoryEntry.CreateWithId(
                    DeterministicId.ForExternalHistory(issue.Id, Source, contact.Column, contact.AtUtc),
                    issue.Id,
                    contact.ActionType,
                    contact.Column,
                    command.Actor,
                    contact.AtUtc));
            }
        }

        if (toSave.Count > 0)
        {
            await issues.SaveBatchAsync(toSave, cancellationToken);
        }

        foreach (var entry in historyEntries)
        {
            await history.AddAsync(entry, cancellationToken);
        }

        if (auditEntries.Count > 0)
        {
            await audit.AppendBatchAsync(auditEntries, cancellationToken);
        }

        if (rejections.Count > 0)
        {
            await imports.AddRejectionsAsync(rejections, cancellationToken);
        }

        var matched = rows.Count - rejections.Count;
        var progress = job.RecordProgress(
            ImportJobCounters.Empty with
            {
                ProcessedRows = rows.Count,
                Matched = matched,
                Unmatched = rejections.Count
            },
            now);

        if (progress.IsFailure)
        {
            return progress.ToFailure<ImportBatchResult>();
        }

        await imports.SaveAsync(job, cancellationToken);

        return new ImportBatchResult(rows.Count, matched, rejections.Count, historyEntries.Count);
    }
}
