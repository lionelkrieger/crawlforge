using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// Paged reads, whitelisted filters, and sorting — the read side must serve
/// dashboard/list surfaces from indexed queries, never full materializations.
/// </summary>
public sealed class PagingFilterSortTests
{
    private static async Task<SqlRepository<FindingDto>> SeedFindingsAsync(TestDatabase database, int count)
    {
        var repository = new SqlRepository<FindingDto>(database.Provider);
        for (var i = 0; i < count; i++)
        {
            await repository.CreateAsync(new FindingDto(
                0, 1, $"rule-{i}", (AuditCategory)(i % 3), (FindingSeverity)(i % 4),
                UrlId: null, EvidenceJson: $"{{\"i\":{i}}}", RecommendationKey: null, IsSuppressed: i % 10 == 0));
        }

        return repository;
    }

    [Fact]
    public async Task ListAsync_Paginates_WithStableTotalCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = await SeedFindingsAsync(database, 120);

        var first = await repository.ListAsync(new ListQuery(Offset: 0, Limit: 25));
        var second = await repository.ListAsync(new ListQuery(Offset: 25, Limit: 25));
        var last = await repository.ListAsync(new ListQuery(Offset: 100, Limit: 25));

        Assert.Equal(120, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(25, second.Items.Count);
        Assert.Equal(20, last.Items.Count);
        Assert.Empty(first.Items.Select(f => f.Id).Intersect(second.Items.Select(f => f.Id)));
        Assert.Equal(0, first.Offset);
        Assert.Equal(25, second.Offset);
    }

    [Fact]
    public async Task ListAsync_FiltersByEnumAndBooleanColumns()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = await SeedFindingsAsync(database, 120);

        var errors = await repository.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["Severity"] = "0" }));
        var unsuppressedWarnings = await repository.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["Severity"] = "1", ["IsSuppressed"] = "false" }));

        Assert.Equal(30, errors.TotalCount);
        Assert.All(errors.Items, f => Assert.Equal(FindingSeverity.Error, f.Severity));
        Assert.Equal(30, unsuppressedWarnings.TotalCount);
        Assert.All(unsuppressedWarnings.Items, f => Assert.False(f.IsSuppressed));
    }

    [Fact]
    public async Task ListAsync_SortsAscendingAndDescending()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = await SeedFindingsAsync(database, 40);

        var ascending = await repository.ListAsync(new ListQuery(SortBy: "Id", SortDescending: false, Limit: 40));
        var descending = await repository.ListAsync(new ListQuery(SortBy: "Id", SortDescending: true, Limit: 40));

        Assert.Equal(ascending.Items.Select(f => f.Id).Order(), ascending.Items.Select(f => f.Id));
        Assert.Equal(ascending.Items.Select(f => f.Id).OrderDescending(), descending.Items.Select(f => f.Id));
    }

    [Fact]
    public async Task ListAsync_RejectsUnknownFilterAndSortFields()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = await SeedFindingsAsync(database, 5);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["NotAColumn"] = "1" })));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListAsync(new ListQuery(SortBy: "NotAColumn")));
    }

    [Fact]
    public async Task ListAsync_ExcludesSoftDeletedProjects()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<ProjectDto>(database.Provider);
        var kept = await repository.CreateAsync(SampleFactory.Create<ProjectDto>() with { Name = "kept" });
        var removed = await repository.CreateAsync(SampleFactory.Create<ProjectDto>() with { Name = "removed" });
        await repository.DeleteAsync(removed.Id);

        var page = await repository.ListAsync(new ListQuery());

        Assert.Equal(2, page.TotalCount); // base chain project + kept
        Assert.DoesNotContain(page.Items, p => p.Id == removed.Id);
        Assert.Contains(page.Items, p => p.Id == kept.Id);
    }

    [Fact]
    public async Task StreamAsync_AppliesFiltersWithoutPaging()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = await SeedFindingsAsync(database, 60);

        var count = 0;
        await foreach (var finding in repository.StreamAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["Category"] = "2" })))
        {
            Assert.Equal((AuditCategory)2, finding.Category);
            count++;
        }

        Assert.Equal(20, count);
    }
}
