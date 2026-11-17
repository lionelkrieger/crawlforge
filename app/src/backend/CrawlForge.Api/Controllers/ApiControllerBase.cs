using CrawlForge.Contracts.Common;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Stable machine-readable error codes of the uniform envelope
/// (FS-CRAWL-001 §10.1/§10.4). Codes are lowercase kebab-case strings.
/// </summary>
public static class ApiErrorCodes
{
    /// <summary>The addressed entity does not exist (HTTP 404).</summary>
    public const string NotFound = "not-found";

    /// <summary>Request input failed validation; field reasons attached (HTTP 400).</summary>
    public const string Validation = "validation";

    /// <summary>The request conflicts with current state (HTTP 409).</summary>
    public const string Conflict = "conflict";

    /// <summary>The entity is in a state that forbids the operation (HTTP 409).</summary>
    public const string InvalidState = "invalid-state";

    /// <summary>The requested operation is not supported (HTTP 400).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>An unexpected local failure occurred (HTTP 500).</summary>
    public const string Internal = "internal";
}

/// <summary>
/// Base type of every loopback REST controller: uniform response envelope
/// helpers (FS-CRAWL-001 §10.1) and shared list-query binding. No authorization
/// exists beyond the loopback boundary (§10.1).
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Maximum page size accepted by list endpoints.</summary>
    protected const int MaxPageSize = 1000;

    /// <summary>Wraps a payload in the uniform envelope with HTTP 200.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="data">Payload.</param>
    /// <param name="pagination">Optional pagination metadata.</param>
    /// <returns>200 envelope result.</returns>
    protected ObjectResult OkEnvelope<T>(T data, PaginationMetadata? pagination = null) =>
        StatusCode(StatusCodes.Status200OK, new ApiEnvelope<T>(data, pagination));

    /// <summary>Wraps a created payload in the uniform envelope with HTTP 201.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="data">Created payload.</param>
    /// <returns>201 envelope result.</returns>
    protected ObjectResult CreatedEnvelope<T>(T data) =>
        StatusCode(StatusCodes.Status201Created, new ApiEnvelope<T>(data));

    /// <summary>Uniform 404 error envelope.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <returns>404 envelope result.</returns>
    protected ObjectResult NotFoundError(string message) =>
        StatusCode(StatusCodes.Status404NotFound, ErrorEnvelope(ApiErrorCodes.NotFound, message));

    /// <summary>Uniform 400 validation error envelope with field-level reasons.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="fieldErrors">Optional field-level reasons (§10.4).</param>
    /// <returns>400 envelope result.</returns>
    protected ObjectResult ValidationError(string message, IReadOnlyList<ApiFieldError>? fieldErrors = null) =>
        StatusCode(StatusCodes.Status400BadRequest, ErrorEnvelope(ApiErrorCodes.Validation, message, fieldErrors));

    /// <summary>Uniform 409 conflict error envelope.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <returns>409 envelope result.</returns>
    protected ObjectResult ConflictError(string message) =>
        StatusCode(StatusCodes.Status409Conflict, ErrorEnvelope(ApiErrorCodes.Conflict, message));

    /// <summary>Uniform 409 invalid-state error envelope.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <returns>409 envelope result.</returns>
    protected ObjectResult InvalidStateError(string message) =>
        StatusCode(StatusCodes.Status409Conflict, ErrorEnvelope(ApiErrorCodes.InvalidState, message));

    /// <summary>Uniform 400 unsupported-operation error envelope.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <returns>400 envelope result.</returns>
    protected ObjectResult UnsupportedError(string message) =>
        StatusCode(StatusCodes.Status400BadRequest, ErrorEnvelope(ApiErrorCodes.Unsupported, message));

    /// <summary>Builds the error envelope (null data payload).</summary>
    /// <param name="code">Machine-readable code.</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="fieldErrors">Optional field-level reasons.</param>
    /// <returns>Error envelope.</returns>
    protected static ApiEnvelope<object?> ErrorEnvelope(string code, string message, IReadOnlyList<ApiFieldError>? fieldErrors = null) =>
        new(null, null, new ApiError(code, message, fieldErrors));

    /// <summary>
    /// Binds offset/limit/sort query parameters into the locked list query,
    /// clamping the page size and rejecting negative offsets.
    /// </summary>
    /// <param name="offset">Requested offset.</param>
    /// <param name="limit">Requested page size.</param>
    /// <param name="sortBy">Optional sort field.</param>
    /// <param name="sortDescending">Sort direction.</param>
    /// <param name="filters">Optional equality filters.</param>
    /// <returns>The bound list query.</returns>
    protected static ListQuery BindListQuery(int offset, int limit, string? sortBy, bool sortDescending, IReadOnlyDictionary<string, string>? filters = null) =>
        new(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), filters, sortBy, sortDescending);

    /// <summary>Projects a paged result into envelope pagination metadata.</summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="page">Paged result.</param>
    /// <returns>Pagination metadata.</returns>
    protected static PaginationMetadata PaginationOf<T>(PagedResult<T> page) =>
        new(page.Offset, page.Limit, page.TotalCount);

    /// <summary>
    /// Binds the wire sort/order query parameters against a per-route whitelist.
    /// Wire field names are camelCase; the repository matches DTO property names
    /// case-insensitively.
    /// </summary>
    /// <param name="sort">Requested sort field, when present.</param>
    /// <param name="order">Requested direction: asc or desc.</param>
    /// <param name="allowedFields">Whitelisted wire sort fields.</param>
    /// <param name="sortBy">Resolved sort field, or null for the default order.</param>
    /// <param name="sortDescending">Resolved direction.</param>
    /// <param name="error">Validation message when binding fails.</param>
    /// <returns>True when the sort parameters are valid.</returns>
    protected static bool TryBindSort(string? sort, string? order, IReadOnlyCollection<string> allowedFields, out string? sortBy, out bool sortDescending, out string? error)
    {
        sortBy = null;
        sortDescending = false;
        error = null;

        if (order is not null && order is not ("asc" or "desc"))
        {
            error = $"Unknown sort order '{order}'; use 'asc' or 'desc'.";
            return false;
        }

        sortDescending = order == "desc";
        if (sort is null)
        {
            return true;
        }

        if (!allowedFields.Contains(sort, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Unknown sort field '{sort}'.";
            return false;
        }

        sortBy = sort;
        return true;
    }
}
