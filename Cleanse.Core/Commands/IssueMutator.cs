using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands;

/// <summary>
/// Shared load / check version / mutate / persist / audit pipeline for the single-issue human
/// commands. Keeps the "only audit and bump the version on a material change" rule in exactly
/// one place.
/// </summary>
internal sealed class IssueMutator(IIssueStore issues, IAuditStore audit, TimeProvider clock)
{
    public async Task<Result<IssueDto>> MutateAsync(
        IssueId issueId,
        int? expectedVersion,
        Actor actor,
        Func<Issue, DateTimeOffset, Result<IssueChange>> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var issue = await issues.GetAsync(issueId, cancellationToken);
        if (issue is null)
        {
            return DomainErrors.IssueNotFound(issueId.Value);
        }

        var loadedVersion = issue.Version;
        if (expectedVersion is { } expected && expected != loadedVersion)
        {
            return DomainErrors.ConcurrencyConflict(issueId.Value, expected, loadedVersion);
        }

        var now = clock.GetUtcNow();
        var result = mutate(issue, now);
        if (result.IsFailure)
        {
            return result.ToFailure<IssueDto>();
        }

        var change = result.Value;
        if (!change.IsMaterial)
        {
            return issue.ToDto();
        }

        // Always optimistic against what we read, whether or not the caller supplied a precondition.
        if (!await issues.SaveAsync(issue, loadedVersion, cancellationToken))
        {
            return DomainErrors.ConcurrencyConflict(issueId.Value, loadedVersion, loadedVersion + 1);
        }

        await audit.AppendAsync(AuditEntry.ForIssue(issueId, change, actor, now), cancellationToken);
        return issue.ToDto();
    }
}
