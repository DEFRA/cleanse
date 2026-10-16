using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Issues;

public sealed record GetIssueQuery(IssueId IssueId) : IQuery<IssueDto>;

internal sealed class GetIssueQueryHandler(IIssueStore issues) : IQueryHandler<GetIssueQuery, IssueDto>
{
    public async Task<Result<IssueDto>> HandleAsync(
        GetIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var issue = await issues.GetAsync(query.IssueId, cancellationToken);
        return issue is null
            ? DomainErrors.IssueNotFound(query.IssueId.Value)
            : issue.ToDto();
    }
}

/// <summary>The triage list. Filter is declarative so every storage provider can honour it.</summary>
public sealed record SearchIssuesQuery(IssueFilter Filter) : IQuery<PagedResult<IssueDto>>;

internal sealed class SearchIssuesQueryHandler(IIssueStore issues)
    : IQueryHandler<SearchIssuesQuery, PagedResult<IssueDto>>
{
    public async Task<Result<PagedResult<IssueDto>>> HandleAsync(
        SearchIssuesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Filter);

        var page = await issues.SearchAsync(query.Filter, cancellationToken);
        return page.Map(issue => issue.ToDto());
    }
}
