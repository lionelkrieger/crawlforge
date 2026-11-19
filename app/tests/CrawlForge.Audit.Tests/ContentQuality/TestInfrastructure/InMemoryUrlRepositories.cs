using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// Thread-safe in-memory <see cref="IReadRepository{UrlRecordDto}"/> double.
/// Mirrors SqlRepository semantics: case-insensitive equality filters on DTO
/// property names, key-column default sort, offset/limit paging with the
/// filtered total count.
/// </summary>
internal sealed class InMemoryUrlRecordRepository : IReadRepository<UrlRecordDto>
{
    private readonly object _gate = new();
    private readonly List<UrlRecordDto> _records = [];

    public void Add(UrlRecordDto record)
    {
        lock (_gate)
        {
            _records.Add(record);
        }
    }

    public Task<UrlRecordDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_records.FirstOrDefault(r => r.Id == id));
        }
    }

    public Task<PagedResult<UrlRecordDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IEnumerable<UrlRecordDto> filtered = _records;
            if (query.Filters is not null)
            {
                foreach (var (field, value) in query.Filters)
                {
                    filtered = field.ToLowerInvariant() switch
                    {
                        "auditid" => filtered.Where(r => r.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                        "id" => filtered.Where(r => r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                        "statusclass" => filtered.Where(r => ((int)r.StatusClass).ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                        _ => throw new ArgumentException($"Unknown filter field '{field}' for url_records."),
                    };
                }
            }

            var ordered = query.SortBy?.ToLowerInvariant() switch
            {
                "id" or null or "" => filtered.OrderBy(r => r.Id),
                "canonicalurl" => filtered.OrderBy(r => r.CanonicalUrl, StringComparer.Ordinal),
                _ => throw new ArgumentException($"Unknown sort field '{query.SortBy}' for url_records."),
            };
            var materialized = ordered.ToList();
            if (query.SortDescending)
            {
                materialized.Reverse();
            }

            var items = materialized.Skip(query.Offset).Take(query.Limit).ToList();
            return Task.FromResult(new PagedResult<UrlRecordDto>(items, materialized.Count, query.Offset, query.Limit));
        }
    }
}

/// <summary>
/// Thread-safe in-memory <see cref="IReadRepository{UrlElementDto}"/> double
/// with the same filter/sort semantics as <see cref="InMemoryUrlRecordRepository"/>.
/// </summary>
internal sealed class InMemoryUrlElementRepository : IReadRepository<UrlElementDto>
{
    private readonly object _gate = new();
    private readonly List<UrlElementDto> _elements = [];

    public void Add(UrlElementDto element)
    {
        lock (_gate)
        {
            _elements.Add(element);
        }
    }

    public Task<UrlElementDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_elements.FirstOrDefault(e => e.Id == id));
        }
    }

    public Task<PagedResult<UrlElementDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IEnumerable<UrlElementDto> filtered = _elements;
            if (query.Filters is not null)
            {
                foreach (var (field, value) in query.Filters)
                {
                    filtered = field.ToLowerInvariant() switch
                    {
                        "urlid" => filtered.Where(e => e.UrlId.ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                        "elementgroup" => filtered.Where(e => string.Equals(e.ElementGroup, value, StringComparison.Ordinal)),
                        "id" => filtered.Where(e => e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                        _ => throw new ArgumentException($"Unknown filter field '{field}' for url_elements."),
                    };
                }
            }

            var materialized = filtered.OrderBy(e => e.Id).ToList();
            var items = materialized.Skip(query.Offset).Take(query.Limit).ToList();
            return Task.FromResult(new PagedResult<UrlElementDto>(items, materialized.Count, query.Offset, query.Limit));
        }
    }
}
