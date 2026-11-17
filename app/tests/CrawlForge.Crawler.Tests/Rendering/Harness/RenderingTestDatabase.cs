using System.Data.Common;
using System.Reflection;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Per-test isolated database + REAL <see cref="CompressedContentStore"/> with
/// the production migrated schema and a seeded base reference chain, so
/// rendered DOM + screenshot artifacts persist through the real store.
/// </summary>
public sealed class RenderingTestDatabase : IAsyncDisposable
{
    private RenderingTestDatabase(string rootDirectory, TestSqliteProvider provider, CompressedContentStore store)
    {
        RootDirectory = rootDirectory;
        Provider = provider;
        Store = store;
    }

    /// <summary>Temporary root directory owned by this fixture.</summary>
    public string RootDirectory { get; }

    /// <summary>The test-local provider.</summary>
    public TestSqliteProvider Provider { get; }

    /// <summary>The real compressed content store beside the database.</summary>
    public CompressedContentStore Store { get; }

    /// <summary>Creates a fresh migrated database with the base chain seeded.</summary>
    /// <returns>The fixture; dispose to delete all files.</returns>
    public static async Task<RenderingTestDatabase> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp0206-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new TestSqliteProvider(Path.Combine(root, "crawlforge.db"));
        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync();

        await SeedAsync<ProjectDto>(provider);
        await SeedAsync<ContentScopeProfileDto>(provider);
        await SeedAsync<CrawlConfigurationDto>(provider);
        await SeedAsync<AuditDto>(provider);
        await SeedAsync<UrlRecordDto>(provider);

        var store = new CompressedContentStore(Path.Combine(root, "content"), provider);
        return new RenderingTestDatabase(root, provider, store);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task SeedAsync<TDto>(IDbProvider provider)
        where TDto : class
    {
        var repository = new SqlRepository<TDto>(provider);
        await repository.CreateAsync((TDto)SampleFactory.Create(typeof(TDto)));
    }
}

/// <summary>Test-local SQLite provider (real embedded engine, WAL, foreign keys).</summary>
public sealed class TestSqliteProvider : IDbProvider
{
    private readonly string _connectionString;

    /// <summary>Creates the provider for a file-backed database.</summary>
    /// <param name="databasePath">Local database file path.</param>
    public TestSqliteProvider(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
        }.ToString();
    }

    /// <inheritdoc />
    public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;

    /// <inheritdoc />
    public string Dialect => "sqlite";

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <inheritdoc />
    public async Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return new DbConnectionTestResult(true, true, true, null);
    }
}

/// <summary>
/// Deterministic reflection-based DTO sample factory (same recipe as the
/// phase-01 repository harness): identifier 0, foreign keys bound to the
/// seeded base chain (all 1), live (undeleted) markers.
/// </summary>
public static class SampleFactory
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, long> ForeignKeyIds = new Dictionary<string, long>
    {
        ["ProjectId"] = 1,
        ["ConfigurationId"] = 1,
        ["AuditId"] = 1,
        ["UrlId"] = 1,
        ["SourceUrlId"] = 1,
        ["TargetUrlId"] = 1,
        ["ScheduleDefinitionId"] = 1,
        ["LogImportId"] = 1,
        ["ContentScopeProfileId"] = 1,
    };

    /// <summary>Creates a fully populated sample of the given DTO record type.</summary>
    /// <param name="dtoType">The DTO record type.</param>
    /// <returns>A sample instance with identifier 0 and valid base-chain references.</returns>
    public static object Create(Type dtoType)
    {
        var constructor = dtoType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = constructor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            args[i] = GenerateValue(parameters[i].ParameterType, parameters[i].Name!);
        }

        var instance = constructor.Invoke(args);
        var parameterNames = new HashSet<string>(parameters.Select(p => p.Name!), StringComparer.OrdinalIgnoreCase);
        foreach (var property in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || parameterNames.Contains(property.Name))
            {
                continue;
            }

            property.SetValue(instance, GenerateValue(property.PropertyType, property.Name));
        }

        return instance;
    }

    private static object? GenerateValue(Type type, string name)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return GenerateValue(underlying, name);
        }

        if (type == typeof(long))
        {
            if (string.Equals(name, "Id", StringComparison.OrdinalIgnoreCase))
            {
                return 0L;
            }

            return ForeignKeyIds.TryGetValue(name, out var reference) ? reference : 42L;
        }

        if (type == typeof(int))
        {
            return 3;
        }

        if (type == typeof(double))
        {
            return 0.75;
        }

        if (type == typeof(bool))
        {
            return !string.Equals(name, "IsDeleted", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "IsSuppressed", StringComparison.OrdinalIgnoreCase);
        }

        if (type == typeof(DateTimeOffset))
        {
            return BaseTime;
        }

        if (type == typeof(string))
        {
            if (name.EndsWith("Json", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"sample\":true}";
            }

            if (name.Contains("Url", StringComparison.OrdinalIgnoreCase))
            {
                return "https://example.test/" + name.ToLowerInvariant();
            }

            return "sample-" + name;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).GetValue(0)!;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IList<>)
                || definition == typeof(IEnumerable<>) || definition == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                var listType = typeof(List<>).MakeGenericType(elementType);
                var list = Activator.CreateInstance(listType)!;
                var add = listType.GetMethod("Add")!;
                add.Invoke(list, [GenerateValue(elementType, name + "Item")]);
                return list;
            }
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(GenerateValue(elementType, name + "Item"), 0);
            return array;
        }

        if (type.IsClass && type != typeof(object))
        {
            return Create(type);
        }

        throw new NotSupportedException($"No sample generator for {type} ({name}).");
    }
}
