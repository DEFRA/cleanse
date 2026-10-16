using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Runs;

public sealed record GetRunQuery(string RunId) : IQuery<RuleRunDto>;

internal sealed class GetRunQueryHandler(IRuleRunStore runs, TimeProvider clock)
    : IQueryHandler<GetRunQuery, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        GetRunQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.RunId))
        {
            return DomainErrors.Validation("A run id is required.");
        }

        var run = await runs.GetAsync(query.RunId.Trim(), cancellationToken);
        return run is null ? DomainErrors.RunNotFound(query.RunId) : run.ToDto(clock.GetUtcNow());
    }
}

/// <summary>
/// Run history for a group, newest first, including whatever is running right now. Pass no
/// group to see every group's runs interleaved.
/// </summary>
public sealed record SearchRunsQuery(string? GroupId, IReadOnlyCollection<RunState>? States, PageRequest? Page = null)
    : IQuery<PagedResult<RuleRunSummaryDto>>;

internal sealed class SearchRunsQueryHandler(IRuleRunStore runs, TimeProvider clock)
    : IQueryHandler<SearchRunsQuery, PagedResult<RuleRunSummaryDto>>
{
    public async Task<Result<PagedResult<RuleRunSummaryDto>>> HandleAsync(
        SearchRunsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var groupId = string.IsNullOrWhiteSpace(query.GroupId) ? null : query.GroupId.Trim();
        var page = await runs.SearchAsync(
            groupId, query.States, query.Page ?? PageRequest.Default, cancellationToken);

        var now = clock.GetUtcNow();
        return page.Map(run => run.ToSummaryDto(now));
    }
}

public sealed record GetLatestRunForGroupQuery(string GroupId) : IQuery<RuleRunDto>;

internal sealed class GetLatestRunForGroupQueryHandler(IRuleRunStore runs, TimeProvider clock)
    : IQueryHandler<GetLatestRunForGroupQuery, RuleRunDto>
{
    public async Task<Result<RuleRunDto>> HandleAsync(
        GetLatestRunForGroupQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.GroupId))
        {
            return DomainErrors.Validation("A group id is required.");
        }

        var run = await runs.GetLatestForGroupAsync(query.GroupId.Trim(), cancellationToken);
        return run is null
            ? DomainErrors.RunNotFound($"latest for group '{query.GroupId}'")
            : run.ToDto(clock.GetUtcNow());
    }
}

/// <summary>Dashboard projection: the latest run plus a status breakdown for the group.</summary>
public sealed record GetGroupSummaryQuery(string GroupId) : IQuery<GroupSummaryDto>;

internal sealed class GetGroupSummaryQueryHandler(IIssueStore issues, IRuleRunStore runs, TimeProvider clock)
    : IQueryHandler<GetGroupSummaryQuery, GroupSummaryDto>
{
    public async Task<Result<GroupSummaryDto>> HandleAsync(
        GetGroupSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.GroupId))
        {
            return DomainErrors.Validation("A group id is required.");
        }

        var groupId = query.GroupId.Trim();
        var counts = await issues.CountByStatusAsync(groupId, cancellationToken);
        var latest = await runs.GetLatestForGroupAsync(groupId, cancellationToken);

        var active = counts.Where(c => c.Status == IssueStatus.Active).Sum(c => c.Count);
        var inactive = counts.Where(c => c.Status == IssueStatus.Inactive).Sum(c => c.Count);

        return new GroupSummaryDto(
            groupId,
            latest?.ToDto(clock.GetUtcNow()),
            active + inactive,
            active,
            inactive,
            [.. counts.Select(c => c.ToDto())]);
    }
}
