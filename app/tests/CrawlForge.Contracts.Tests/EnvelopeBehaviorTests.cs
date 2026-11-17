using CrawlForge.Contracts.Common;
using Xunit;

namespace CrawlForge.Contracts.Tests;

/// <summary>
/// Behavior of the uniform REST envelope, pagination, and error DTOs
/// (FS-CRAWL-001 §10.1/§10.4).
/// </summary>
public class EnvelopeBehaviorTests
{
    [Fact]
    public void EnvelopeCarriesDataAndPagination()
    {
        PaginationMetadata pagination = new(Offset: 10, Limit: 50, TotalCount: 123);
        ApiEnvelope<int> envelope = new(42, pagination);

        Assert.Equal(42, envelope.Data);
        Assert.NotNull(envelope.Pagination);
        Assert.Equal(10, envelope.Pagination.Offset);
        Assert.Equal(50, envelope.Pagination.Limit);
        Assert.Equal(123, envelope.Pagination.TotalCount);
        Assert.Null(envelope.Error);
    }

    [Fact]
    public void EnvelopeCarriesFieldLevelValidationErrors()
    {
        ApiFieldError[] fieldErrors =
        {
            new("name", "Name must not be empty."),
            new("depth", "Depth must be positive."),
        };
        ApiError error = new("validation_failed", "The request is invalid.", fieldErrors);
        ApiEnvelope<string> envelope = new(null, null, error);

        Assert.Null(envelope.Data);
        Assert.NotNull(envelope.Error);
        Assert.Equal("validation_failed", envelope.Error.Code);
        Assert.NotNull(envelope.Error.FieldErrors);
        Assert.Equal(2, envelope.Error.FieldErrors.Count);
        Assert.Equal("name", envelope.Error.FieldErrors[0].Field);
    }

    [Fact]
    public void ListQueryHasBaselineDefaults()
    {
        ListQuery query = new();
        Assert.Equal(0, query.Offset);
        Assert.Equal(50, query.Limit);
        Assert.Null(query.Filters);
        Assert.Null(query.SortBy);
        Assert.False(query.SortDescending);
    }

    [Fact]
    public void PagedResultCarriesItemsAndTotals()
    {
        string[] items = { "a", "b" };
        PagedResult<string> page = new(items, TotalCount: 100, Offset: 0, Limit: 2);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(100, page.TotalCount);
        Assert.Equal(0, page.Offset);
        Assert.Equal(2, page.Limit);
    }

    [Fact]
    public void EnvelopeDefaultsAreOptional()
    {
        ApiEnvelope<int> envelope = new(7);
        Assert.Null(envelope.Pagination);
        Assert.Null(envelope.Error);
    }
}
