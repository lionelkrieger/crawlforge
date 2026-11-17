using CrawlForge.Contracts.Common;

namespace CrawlForge.Contracts.Persistence;

/// <summary>
/// CQRS read repository (FS-CRAWL-001 §9): query-side access to one entity
/// type with offset/limit pagination, filtering, and sorting.
/// </summary>
/// <typeparam name="TDto">Entity DTO type served by this repository.</typeparam>
public interface IReadRepository<TDto>
    where TDto : class
{
    /// <summary>Reads one entity by identifier.</summary>
    /// <param name="id">Entity identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entity, or null when absent.</returns>
    Task<TDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Lists entities matching a query.</summary>
    /// <param name="query">Pagination, filter, and sort specification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of entities with the total count.</returns>
    Task<PagedResult<TDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// CQRS write repository (FS-CRAWL-001 §9): command-side access to one entity
/// type. Projects and audits support soft delete with transactional pruning.
/// </summary>
/// <typeparam name="TDto">Entity DTO type served by this repository.</typeparam>
public interface IWriteRepository<TDto>
    where TDto : class
{
    /// <summary>Creates an entity.</summary>
    /// <param name="entity">Entity payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted entity with its identifier.</returns>
    Task<TDto> CreateAsync(TDto entity, CancellationToken cancellationToken = default);

    /// <summary>Updates an entity.</summary>
    /// <param name="entity">Entity payload carrying the identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    Task<TDto> UpdateAsync(TDto entity, CancellationToken cancellationToken = default);

    /// <summary>Deletes an entity (soft delete where the pattern applies).</summary>
    /// <param name="id">Entity identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the entity was deleted.</returns>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);
}
