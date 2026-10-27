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
    IUserStore users,
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

        var auditEntries = new List<AuditEntry>();
        var historyEntries = new List<IssueHistoryEntry>();
        var rejections = new List<ImportJobRejection>();
        var directory = new List<KnownUser>();
        if (job.TenantId is not null && job.ClientId is not null)
        {
            for (var pageNumber = 1; ; pageNumber++)
            {
                var page = await users.SearchAsync(
                    job.TenantId, job.ClientId, new PageRequest(pageNumber, PageRequest.MaxPageSize), cancellationToken);
                directory.AddRange(page.Items.Where(user => !string.IsNullOrWhiteSpace(user.FullName)));
                if ((long)pageNumber * PageRequest.MaxPageSize >= page.TotalCount)
                {
                    break;
                }
            }
        }

        var byName = directory.GroupBy(user => user.FullName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matched = 0;

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

            if (!string.Equals(issue.GroupId, job.GroupId, StringComparison.Ordinal))
            {
                rejections.Add(new ImportJobRejection(job.Id, row.RowNumber, row.IssueId.Value,
                    ImportRejectionReasons.IssueNotFound, "No issue exists in the import group."));
                continue;
            }

            matched++;
            var expectedVersion = issue.Version;
            if (row.HasAssignee)
            {
                var name = string.IsNullOrWhiteSpace(row.AssignedTo) ? null : row.AssignedTo.Trim();
                byName.TryGetValue(name ?? string.Empty, out var candidates);
                var assignment = candidates is { Length: 1 }
                    ? issue.AssignKnownUser(candidates[0], now)
                    : issue.AssignTo(name, now);
                if (assignment.IsMaterial)
                {
                    auditEntries.Add(AuditEntry.ForIssue(issue.Id, assignment, command.Actor, now, job.Id.ToString()));
                }

                if (name is not null && candidates is not { Length: 1 })
                {
                    var reason = job.TenantId is null
                        ? ImportRejectionReasons.AssigneeScopeMissing
                        : candidates is { Length: > 1 }
                            ? ImportRejectionReasons.AssigneeAmbiguous
                            : ImportRejectionReasons.AssigneeUnmatched;
                    rejections.Add(new ImportJobRejection(job.Id, row.RowNumber, row.IssueId.Value,
                        reason, "Assignment warning: legacy assignee retained; no unique scoped known user was selected."));
                }
            }

            var change = issue.ApplyExternalState(row.ToState(), now);
            if (change.IsMaterial)
            {
                auditEntries.Add(AuditEntry.ForIssue(issue.Id, change, command.Actor, now, job.Id.ToString()));
            }

            if (issue.Version != expectedVersion && !await issues.SaveAsync(issue, expectedVersion, cancellationToken))
            {
                return DomainErrors.ConcurrencyConflict(issue.Id.Value, expectedVersion, expectedVersion + 1);
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

        var unmatched = rows.Count - matched;
        var progress = job.RecordProgress(
            ImportJobCounters.Empty with
            {
                ProcessedRows = rows.Count,
                Matched = matched,
                Unmatched = unmatched
            },
            now);

        if (progress.IsFailure)
        {
            return progress.ToFailure<ImportBatchResult>();
        }

        await imports.SaveAsync(job, cancellationToken);

        return new ImportBatchResult(rows.Count, matched, unmatched, historyEntries.Count);
    }
}
