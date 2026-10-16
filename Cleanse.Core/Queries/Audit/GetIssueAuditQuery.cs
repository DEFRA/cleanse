using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Audit;

/// <summary>The system change log for one issue, newest first.</summary>
public sealed record GetIssueAuditQuery(IssueId IssueId, PageRequest? Page = null)
    : IQuery<PagedResult<AuditEntryDto>>;

internal sealed class GetIssueAuditQueryHandler(IAuditStore audit)
    : IQueryHandler<GetIssueAuditQuery, PagedResult<AuditEntryDto>>
{
    public async Task<Result<PagedResult<AuditEntryDto>>> HandleAsync(
        GetIssueAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await audit.GetForIssueAsync(query.IssueId, query.Page ?? PageRequest.Default, cancellationToken);
        return page.Map(entry => entry.ToDto());
    }
}

/// <summary>Run lifecycle events, including the end-of-run sweep summary.</summary>
public sealed record GetRunAuditQuery(string RunId, PageRequest? Page = null)
    : IQuery<PagedResult<AuditEntryDto>>;

internal sealed class GetRunAuditQueryHandler(IAuditStore audit)
    : IQueryHandler<GetRunAuditQuery, PagedResult<AuditEntryDto>>
{
    public async Task<Result<PagedResult<AuditEntryDto>>> HandleAsync(
        GetRunAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.RunId))
        {
            return DomainErrors.Validation("A run id is required.");
        }

        var page = await audit.GetForRunAsync(
            query.RunId.Trim(), query.Page ?? PageRequest.Default, cancellationToken);
        return page.Map(entry => entry.ToDto());
    }
}
