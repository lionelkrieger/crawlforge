namespace CrawlForge.Contracts.Common;

/// <summary>
/// Uniform REST response envelope (FS-CRAWL-001 §10.1): a data payload, optional
/// pagination metadata, and an optional error object. Every loopback REST route
/// returns this shape; extension routes reuse it unchanged.
/// </summary>
/// <typeparam name="T">Type of the data payload.</typeparam>
/// <param name="Data">The payload; null when the request failed.</param>
/// <param name="Pagination">Optional pagination metadata for list responses.</param>
/// <param name="Error">Optional machine-readable error with a human-readable message.</param>
public sealed record ApiEnvelope<T>(
    T? Data,
    PaginationMetadata? Pagination = null,
    ApiError? Error = null);

/// <summary>
/// Offset/limit pagination metadata returned with list responses
/// (FS-CRAWL-001 §10.1).
/// </summary>
/// <param name="Offset">Zero-based offset of the returned page.</param>
/// <param name="Limit">Maximum number of items requested for the page.</param>
/// <param name="TotalCount">Total number of items matching the query.</param>
public sealed record PaginationMetadata(int Offset, int Limit, long TotalCount);

/// <summary>
/// Machine-readable error object of the uniform envelope (FS-CRAWL-001 §10.1).
/// Validation errors carry field-level reasons (FS-CRAWL-001 §10.4).
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable error message.</param>
/// <param name="FieldErrors">Optional field-level validation reasons.</param>
public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyList<ApiFieldError>? FieldErrors = null);

/// <summary>
/// A single field-level validation reason inside an <see cref="ApiError"/>.
/// </summary>
/// <param name="Field">Name of the offending input field.</param>
/// <param name="Reason">Human-readable reason the field was rejected.</param>
public sealed record ApiFieldError(string Field, string Reason);
