namespace CrawlForge.Contracts.Common;

/// <summary>
/// Internal paged result used by read repositories and list surfaces; the REST
/// layer projects it into <see cref="ApiEnvelope{T}"/> plus
/// <see cref="PaginationMetadata"/>.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">The returned page of items.</param>
/// <param name="TotalCount">Total number of items matching the query.</param>
/// <param name="Offset">Zero-based offset of the returned page.</param>
/// <param name="Limit">Maximum number of items requested for the page.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    long TotalCount,
    int Offset,
    int Limit);

/// <summary>
/// Offset/limit list query with filtering and sorting (FS-CRAWL-001 §10.1:
/// list endpoints accept pagination, filtering, and sorting).
/// </summary>
/// <param name="Offset">Zero-based offset; defaults to 0.</param>
/// <param name="Limit">Page size; defaults to 50.</param>
/// <param name="Filters">Optional equality filters keyed by field name.</param>
/// <param name="SortBy">Optional sort field name.</param>
/// <param name="SortDescending">Whether sorting is descending.</param>
public sealed record ListQuery(
    int Offset = 0,
    int Limit = 50,
    IReadOnlyDictionary<string, string>? Filters = null,
    string? SortBy = null,
    bool SortDescending = false);
