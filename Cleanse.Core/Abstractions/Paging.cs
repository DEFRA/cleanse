namespace Cleanse.Core.Abstractions;

public sealed record PageRequest
{
    public const int MaxPageSize = 500;

    public static readonly PageRequest Default = new(1, 50);

    public PageRequest(int pageNumber, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);

        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    public int PageNumber { get; }

    public int PageSize { get; }

    public int Skip => (PageNumber - 1) * PageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, long TotalCount)
{
    public static PagedResult<T> Empty(PageRequest page) => new([], page.PageNumber, page.PageSize, 0);

    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public PagedResult<TOther> Map<TOther>(Func<T, TOther> selector) =>
        new([.. Items.Select(selector)], PageNumber, PageSize, TotalCount);
}
