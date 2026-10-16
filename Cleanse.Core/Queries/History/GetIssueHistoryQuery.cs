using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.History;

/// <summary>Human case actions recorded against an issue, newest first.</summary>
public sealed record GetIssueHistoryQuery(IssueId IssueId, PageRequest? Page = null)
    : IQuery<PagedResult<IssueHistoryEntryDto>>;

internal sealed class GetIssueHistoryQueryHandler(IIssueHistoryStore history)
    : IQueryHandler<GetIssueHistoryQuery, PagedResult<IssueHistoryEntryDto>>
{
    public async Task<Result<PagedResult<IssueHistoryEntryDto>>> HandleAsync(
        GetIssueHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await history.GetForIssueAsync(
            query.IssueId, query.Page ?? PageRequest.Default, cancellationToken);
        return page.Map(entry => entry.ToDto());
    }
}
