namespace Authorization.Api.Contracts;

/// <summary>
/// Standard envelope for a server-side page of a collection. <see cref="Total"/> is the total
/// number of rows matching the query across all pages (so the UI can render "N of M" and page
/// counts), while <see cref="Items"/> holds only the current page.
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>
/// Normalizes optional <c>page</c>/<c>pageSize</c> query parameters into safe, clamped values.
/// Both must be supplied and positive to enable paging; otherwise <see cref="Enabled"/> is false
/// and the caller returns the full (unpaged) collection — preserving backward-compatible behaviour
/// for aggregate consumers that need every row.
/// </summary>
public readonly record struct PageRequest(bool Enabled, int Page, int PageSize)
{
    /// <summary>Upper bound on page size to prevent unbounded pulls.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Default page size applied when a page number is given without an explicit size.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>
    /// Builds a clamped <see cref="PageRequest"/>. Paging is enabled when either query parameter is
    /// present; <paramref name="page"/> is clamped to at least 1 and <paramref name="pageSize"/> to
    /// the range <c>1..<see cref="MaxPageSize"/></c>.
    /// </summary>
    public static PageRequest From(int? page, int? pageSize)
    {
        if (page is null && pageSize is null)
        {
            return new PageRequest(false, 1, DefaultPageSize);
        }

        int resolvedPage = page is int p && p > 0 ? p : 1;
        int resolvedPageSize = pageSize is int s && s > 0 ? Math.Min(s, MaxPageSize) : DefaultPageSize;
        return new PageRequest(true, resolvedPage, resolvedPageSize);
    }

    /// <summary>Number of rows to skip for the current page.</summary>
    public int Skip => (Page - 1) * PageSize;
}
