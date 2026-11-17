using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// CQRS repository correctness: create/read round-trips for every catalog entity
/// (including additive members and JSON payloads), update/delete semantics, and
/// soft delete.
/// </summary>
public sealed class RepositoryCrudTests
{
    /// <summary>All catalog DTO types, one theory case each.</summary>
    public static IEnumerable<object[]> AllCatalogTypes() =>
        SchemaNames.AllEntities.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllCatalogTypes))]
    public async Task CreateRead_RoundTrips_EveryCatalogEntity(Type dtoType)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sample = SampleFactory.Create(dtoType);
        var map = EntityMap.For(dtoType);

        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var created = await RepositoryOps.CreateAsync(map, database.Provider.Kind, sample, connection, transaction);
        await transaction.CommitAsync();

        var newId = Convert.ToInt64(map.KeyColumn.GetValue(created));
        Assert.True(newId > 0);

        object? readBack = null;
        await foreach (var row in RepositoryOps.StreamAsync(
            map, database.Provider, "id = @id", new Dictionary<string, object?> { ["id"] = newId }))
        {
            readBack = row;
        }

        Assert.NotNull(readBack);
        Assert.Equal(JsonSerializer.Serialize(sample, dtoType), JsonSerializer.Serialize(readBack, dtoType));
    }

    [Fact]
    public async Task GetById_ReturnsPersistedEntity()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<ProjectDto>(database.Provider);
        var created = await repository.CreateAsync(SampleFactory.Create<ProjectDto>() with { Name = "round-trip" });

        var fetched = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal("round-trip", fetched!.Name);
        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task GetById_MissingRow_ReturnsNull()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<ProjectDto>(database.Provider);

        Assert.Null(await repository.GetByIdAsync(99_999));
    }

    [Fact]
    public async Task Update_PersistsAllChangedColumns()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<ProjectDto>(database.Provider);
        var created = await repository.CreateAsync(SampleFactory.Create<ProjectDto>());

        var updated = created with
        {
            Name = "renamed",
            Description = "new description",
            TagsJson = "[\"a\",\"b\"]",
            UpdatedAt = SampleFactory.BaseTime.AddDays(1),
            ConsentModeEnabled = false
        };
        await repository.UpdateAsync(updated);

        var fetched = await repository.GetByIdAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("renamed", fetched!.Name);
        Assert.Equal("new description", fetched.Description);
        Assert.Equal("[\"a\",\"b\"]", fetched.TagsJson);
        Assert.Equal(SampleFactory.BaseTime.AddDays(1), fetched.UpdatedAt);
        Assert.False(fetched.ConsentModeEnabled);
    }

    [Fact]
    public async Task Update_MissingRow_Throws()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<ProjectDto>(database.Provider);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            repository.UpdateAsync(SampleFactory.Create<ProjectDto>() with { Id = 99_999 }));
    }

    [Fact]
    public async Task Delete_HardDelete_RemovesRow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<UrlElementDto>(database.Provider);
        var created = await repository.CreateAsync(SampleFactory.Create<UrlElementDto>());

        Assert.True(await repository.DeleteAsync(created.Id));
        Assert.Null(await repository.GetByIdAsync(created.Id));
        Assert.False(await repository.DeleteAsync(created.Id));
    }

    [Fact]
    public async Task Delete_SoftDelete_HidesRowButKeepsHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<ProjectDto>(database.Provider);
        var created = await repository.CreateAsync(SampleFactory.Create<ProjectDto>());

        Assert.True(await repository.DeleteAsync(created.Id));

        Assert.Null(await repository.GetByIdAsync(created.Id));
        var page = await repository.ListAsync(new ListQuery());
        Assert.DoesNotContain(page.Items, p => p.Id == created.Id);
        // Soft delete keeps history: base-chain project + the soft-deleted row.
        Assert.Equal(2, await database.CountAsync("projects"));
        Assert.Equal(1L, await database.ScalarAsync($"SELECT is_deleted FROM projects WHERE id = {created.Id}"));
    }

    [Fact]
    public async Task Create_AssignsDistinctSequentialIds()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<SavedViewDto>(database.Provider);

        var first = await repository.CreateAsync(SampleFactory.Create<SavedViewDto>());
        var second = await repository.CreateAsync(SampleFactory.Create<SavedViewDto>());

        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public async Task JsonPayloads_RoundTripNestedAdditiveRecords()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlRepository<CrawlConfigurationDto>(database.Provider);
        var sample = SampleFactory.Create<CrawlConfigurationDto>() with
        {
            LimitOverrides = new Contracts.Crawl.CrawlLimitOverrides(MaxUrls: 5000, MaxDepth: 6, RequestsPerSecond: 2.5),
            ContentScope = new Contracts.Audit.ContentScopeDefinition(["main", "article"], ["nav", "footer"]),
            SerpBudgets = new Contracts.Audit.SerpBudgetSettings(580, 920, "default")
        };

        var created = await repository.CreateAsync(sample);
        var fetched = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal(5000, fetched!.LimitOverrides!.MaxUrls);
        Assert.Equal(2.5, fetched.LimitOverrides.RequestsPerSecond);
        Assert.Equal(new[] { "main", "article" }, fetched.ContentScope!.IncludeSelectors);
        Assert.Equal(new[] { "nav", "footer" }, fetched.ContentScope.ExcludeSelectors);
        Assert.Equal(580, fetched.SerpBudgets!.TitleBudgetPixels);
    }
}
