using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Data.Migrations.Schema;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// WP-01-04 reopen regression suite: the repository mapping layer is verified
/// against the REAL migrated schema (the accepted SchemaCatalog authority),
/// never against EntityMap-derived DDL. Covers the mapping-vs-catalog
/// consistency sweep for all 40 entities and create/read round-trips for every
/// entity, including the reported complex-payload column defects.
/// </summary>
public class SchemaMappingRegressionTests
{
    /// <summary>All registered entity types (theory data).</summary>
    /// <returns>One row per catalog DTO type.</returns>
    public static IEnumerable<object[]> AllEntityTypes() =>
        SchemaNames.AllEntities.Select(t => new object[] { t });

    /// <summary>
    /// Every DTO property must map to a column that physically exists in the
    /// real catalog table. Fails with the complete mismatch list so a single
    /// run enumerates the whole sweep.
    /// </summary>
    [Fact]
    public void EveryMappedColumnExistsInRealCatalog()
    {
        var tables = SchemaCatalog.AllTables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var dtoType in SchemaNames.AllEntities)
        {
            var map = EntityMap.For(dtoType);
            if (!tables.TryGetValue(map.TableName, out var table))
            {
                violations.Add($"{dtoType.Name}: table '{map.TableName}' is not in the catalog.");
                continue;
            }

            var physical = new HashSet<string>(table.Columns.Select(c => c.Name), StringComparer.Ordinal)
            {
                "id",
            };
            foreach (var column in map.Columns)
            {
                if (!physical.Contains(column.ColumnName))
                {
                    violations.Add($"{dtoType.Name}.{column.PropertyName} -> '{column.ColumnName}' (table '{map.TableName}')");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Mapping-vs-catalog mismatches ({violations.Count}):\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// Every NOT NULL catalog column must be mapped by its DTO, otherwise
    /// repository INSERTs cannot satisfy the real schema.
    /// </summary>
    [Fact]
    public void EveryNotNullCatalogColumnIsMapped()
    {
        var mapsByTable = SchemaNames.AllEntities
            .Select(EntityMap.For)
            .ToDictionary(m => m.TableName, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var table in SchemaCatalog.AllTables)
        {
            if (!mapsByTable.TryGetValue(table.Name, out var map))
            {
                violations.Add($"Table '{table.Name}' has no registered DTO.");
                continue;
            }

            var mapped = new HashSet<string>(map.Columns.Select(c => c.ColumnName), StringComparer.Ordinal);
            foreach (var column in table.Columns.Where(c => !c.Nullable && c.Name != "id"))
            {
                if (!mapped.Contains(column.Name))
                {
                    violations.Add($"Table '{table.Name}' NOT NULL column '{column.Name}' is not mapped by the DTO.");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Unmapped NOT NULL columns ({violations.Count}):\n" + string.Join("\n", violations));
    }

    /// <summary>Create/read round-trip for every catalog entity against the real migrated schema.</summary>
    /// <param name="dtoType">The DTO record type under test.</param>
    /// <returns>A task that completes when the round-trip is verified.</returns>
    [Theory]
    [MemberData(nameof(AllEntityTypes))]
    public async Task CreateReadRoundTripsAgainstRealSchema(Type dtoType)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sample = SampleFactory.Create(dtoType);
        var map = EntityMap.For(dtoType);

        var created = await CreateAsync(database, sample, dtoType);
        var readBack = await GetByIdAsync(database, created, dtoType);

        Assert.NotNull(readBack);
        AssertRoundTrip(created, readBack!, map);
    }

    /// <summary>Reported defect: ProjectDto complex payloads survive the real projects table.</summary>
    /// <returns>A task that completes when the round-trip is verified.</returns>
    [Fact]
    public async Task ProjectComplexPayloadsRoundTrip()
    {
        await using var database = await TestDatabase.CreateAsync();
        var project = SampleFactory.Create<ProjectDto>();
        Assert.NotNull(project.ContentScope);
        Assert.NotNull(project.SerpBudgets);

        var created = (ProjectDto)await CreateAsync(database, project, typeof(ProjectDto));
        var back = (ProjectDto)(await GetByIdAsync(database, created, typeof(ProjectDto)))!;

        Assert.Equal(JsonSerializer.Serialize(project.ContentScope), JsonSerializer.Serialize(back.ContentScope));
        Assert.Equal(JsonSerializer.Serialize(project.SerpBudgets), JsonSerializer.Serialize(back.SerpBudgets));
    }

    /// <summary>Reported defect: CrawlConfigurationDto limit overrides and content scope survive.</summary>
    /// <returns>A task that completes when the round-trip is verified.</returns>
    [Fact]
    public async Task ConfigurationComplexPayloadsRoundTrip()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = SampleFactory.Create<CrawlConfigurationDto>();
        Assert.NotNull(configuration.LimitOverrides);
        Assert.NotNull(configuration.ContentScope);
        Assert.NotNull(configuration.SerpBudgets);

        var created = (CrawlConfigurationDto)await CreateAsync(database, configuration, typeof(CrawlConfigurationDto));
        var back = (CrawlConfigurationDto)(await GetByIdAsync(database, created, typeof(CrawlConfigurationDto)))!;

        Assert.Equal(JsonSerializer.Serialize(configuration.LimitOverrides), JsonSerializer.Serialize(back.LimitOverrides));
        Assert.Equal(JsonSerializer.Serialize(configuration.ContentScope), JsonSerializer.Serialize(back.ContentScope));
        Assert.Equal(JsonSerializer.Serialize(configuration.SerpBudgets), JsonSerializer.Serialize(back.SerpBudgets));
    }

    /// <summary>Reported defect: ApplicationSettingsDto complex members survive the real settings table.</summary>
    /// <returns>A task that completes when the round-trip is verified.</returns>
    [Fact]
    public async Task SettingsComplexPayloadsRoundTrip()
    {
        await using var database = await TestDatabase.CreateAsync();
        var settings = SampleFactory.Create<ApplicationSettingsDto>();
        Assert.NotNull(settings.DefaultCrawlLimits);
        Assert.NotNull(settings.AimdDefaults);
        Assert.NotNull(settings.EmbeddingProvider);
        Assert.NotNull(settings.SerpBudgetDefaults);

        var created = (ApplicationSettingsDto)await CreateAsync(database, settings, typeof(ApplicationSettingsDto));
        var back = (ApplicationSettingsDto)(await GetByIdAsync(database, created, typeof(ApplicationSettingsDto)))!;

        Assert.Equal(JsonSerializer.Serialize(settings.DefaultCrawlLimits), JsonSerializer.Serialize(back.DefaultCrawlLimits));
        Assert.Equal(JsonSerializer.Serialize(settings.AimdDefaults), JsonSerializer.Serialize(back.AimdDefaults));
        Assert.Equal(JsonSerializer.Serialize(settings.EmbeddingProvider), JsonSerializer.Serialize(back.EmbeddingProvider));
        Assert.Equal(JsonSerializer.Serialize(settings.SerpBudgetDefaults), JsonSerializer.Serialize(back.SerpBudgetDefaults));
    }

    /// <summary>Reported defect: MetricSummaryDto.Value persists through the metric_value column.</summary>
    /// <returns>A task that completes when the round-trip is verified.</returns>
    [Fact]
    public async Task MetricValueRoundTrips()
    {
        await using var database = await TestDatabase.CreateAsync();
        var metric = SampleFactory.Create<MetricSummaryDto>() with { Value = 0.75 };

        var created = (MetricSummaryDto)await CreateAsync(database, metric, typeof(MetricSummaryDto));
        var back = (MetricSummaryDto)(await GetByIdAsync(database, created, typeof(MetricSummaryDto)))!;

        Assert.Equal(0.75, back.Value);
        Assert.Equal(0.75, Convert.ToDouble(await database.ScalarAsync(
            $"SELECT metric_value FROM metric_summaries WHERE id = {back.Id};")));
    }

    /// <summary>Sweep verification: AuditDto additive snapshots (incl. resolved content scope) survive.</summary>
    /// <returns>A task that completes when the round-trip is verified.</returns>
    [Fact]
    public async Task AuditAdditiveSnapshotsRoundTrip()
    {
        await using var database = await TestDatabase.CreateAsync();
        var audit = SampleFactory.Create<AuditDto>();
        Assert.NotNull(audit.ResolvedContentScopeJson);

        var created = (AuditDto)await CreateAsync(database, audit, typeof(AuditDto));
        var back = (AuditDto)(await GetByIdAsync(database, created, typeof(AuditDto)))!;

        Assert.Equal(audit.ResolvedContentScopeJson, back.ResolvedContentScopeJson);
        Assert.Equal(audit.WafCatalogueVersion, back.WafCatalogueVersion);
        Assert.Equal(audit.EmbeddingSimilarityThreshold, back.EmbeddingSimilarityThreshold);
        Assert.Equal(audit.GeoTogglesJson, back.GeoTogglesJson);
    }

    private static async Task<object> CreateAsync(TestDatabase database, object sample, Type dtoType)
    {
        var repositoryType = typeof(SqlRepository<>).MakeGenericType(dtoType);
        var repository = Activator.CreateInstance(repositoryType, database.Provider)!;
        var method = repositoryType.GetMethod("CreateAsync", [dtoType, typeof(CancellationToken)])!;
        var task = (Task)method.Invoke(repository, [sample, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static async Task<object?> GetByIdAsync(TestDatabase database, object created, Type dtoType)
    {
        var id = (long)dtoType.GetProperty("Id")!.GetValue(created)!;
        var repositoryType = typeof(SqlRepository<>).MakeGenericType(dtoType);
        var repository = Activator.CreateInstance(repositoryType, database.Provider)!;
        var method = repositoryType.GetMethod("GetByIdAsync", [typeof(long), typeof(CancellationToken)])!;
        var task = (Task)method.Invoke(repository, [id, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static void AssertRoundTrip(object expected, object actual, EntityMap map)
    {
        foreach (var column in map.Columns)
        {
            var expectedValue = column.GetValue(expected);
            var actualValue = column.GetValue(actual);
            if (column.IsJson)
            {
                Assert.Equal(JsonSerializer.Serialize(expectedValue), JsonSerializer.Serialize(actualValue));
            }
            else if (expectedValue is DateTimeOffset expectedTime && actualValue is DateTimeOffset actualTime)
            {
                Assert.Equal(expectedTime.UtcDateTime, actualTime.UtcDateTime);
            }
            else
            {
                Assert.Equal(expectedValue, actualValue);
            }
        }
    }
}
