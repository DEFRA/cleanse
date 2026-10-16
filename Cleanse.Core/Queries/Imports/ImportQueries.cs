using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Imports;

public sealed record GetImportJobQuery(Guid ImportJobId) : IQuery<ImportJobDto>;

internal sealed class GetImportJobQueryHandler(IImportJobStore imports)
    : IQueryHandler<GetImportJobQuery, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> HandleAsync(
        GetImportJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var job = await imports.GetAsync(query.ImportJobId, cancellationToken);
        return job is null ? DomainErrors.ImportNotFound(query.ImportJobId) : job.ToDto();
    }
}

public sealed record SearchImportJobsQuery(string? GroupId = null, PageRequest? Page = null)
    : IQuery<PagedResult<ImportJobDto>>;

internal sealed class SearchImportJobsQueryHandler(IImportJobStore imports)
    : IQueryHandler<SearchImportJobsQuery, PagedResult<ImportJobDto>>
{
    public async Task<Result<PagedResult<ImportJobDto>>> HandleAsync(
        SearchImportJobsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await imports.SearchAsync(
            query.GroupId, query.Page ?? PageRequest.Default, cancellationToken);
        return page.Map(job => job.ToDto());
    }
}

public sealed record GetImportRejectionsQuery(Guid ImportJobId, PageRequest? Page = null)
    : IQuery<PagedResult<ImportRejectionDto>>;

internal sealed class GetImportRejectionsQueryHandler(IImportJobStore imports)
    : IQueryHandler<GetImportRejectionsQuery, PagedResult<ImportRejectionDto>>
{
    public async Task<Result<PagedResult<ImportRejectionDto>>> HandleAsync(
        GetImportRejectionsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await imports.GetRejectionsAsync(
            query.ImportJobId, query.Page ?? PageRequest.Default, cancellationToken);

        return page.Map(r => new ImportRejectionDto(r.RowNumber, r.IssueId, r.Reason, r.Detail));
    }
}
