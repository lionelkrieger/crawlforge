using CrawlForge.Contracts.Entities;
using CrawlForge.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Repositories.Tests.Harness;

/// <summary>
/// Per-test isolated database + content-store root in a temporary directory, with
/// the full catalog schema applied and the base reference chain seeded.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string rootDirectory, SqliteTestProvider provider)
    {
        RootDirectory = rootDirectory;
        Provider = provider;
    }

    /// <summary>Temporary root directory owned by this fixture.</summary>
    public string RootDirectory { get; }

    /// <summary>The test-local provider double.</summary>
    public SqliteTestProvider Provider { get; }

    /// <summary>Database file path.</summary>
    public string DatabasePath => Path.Combine(RootDirectory, "crawlforge.db");

    /// <summary>Content-store root beside the database.</summary>
    public string ContentStoreRoot => Path.Combine(RootDirectory, "content");

    /// <summary>Creates a fresh isolated database with schema and (optionally) the base chain.</summary>
    /// <param name="seedBaseChain">Whether to seed the base reference chain (archive import targets use false).</param>
    /// <returns>The fixture; dispose to delete all files.</returns>
    public static async Task<TestDatabase> CreateAsync(bool seedBaseChain = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp0104-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new SqliteTestProvider(Path.Combine(root, "crawlforge.db"));
        await TestSchema.ApplyAsync(provider);
        var fixture = new TestDatabase(root, provider);
        if (seedBaseChain)
        {
            await fixture.SeedBaseChainAsync();
        }

        return fixture;
    }

    /// <summary>Counts rows in a table (assertions).</summary>
    /// <param name="table">Catalog table name.</param>
    /// <returns>The row count.</returns>
    public async Task<long> CountAsync(string table)
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Executes raw SQL (test setup such as failure-injection triggers).</summary>
    /// <param name="sql">The SQL to execute.</param>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Executes a scalar query (assertions).</summary>
    /// <param name="sql">The SQL to execute.</param>
    /// <returns>The scalar result.</returns>
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task SeedBaseChainAsync()
    {
        await InsertAsync(SampleFactory.Create<ProjectDto>());
        await InsertAsync(SampleFactory.Create<ContentScopeProfileDto>());
        await InsertAsync(SampleFactory.Create<CrawlConfigurationDto>());
        await InsertAsync(SampleFactory.Create<AuditDto>());

        var firstUrl = SampleFactory.Create<UrlRecordDto>() with
        {
            CanonicalUrl = "https://example.test/seed-1",
            Path = "/seed-1"
        };
        await InsertAsync(firstUrl);
        await InsertAsync(firstUrl with
        {
            CanonicalUrl = "https://example.test/seed-2",
            Path = "/seed-2"
        });

        await InsertAsync(SampleFactory.Create<ScheduleDefinitionDto>());
        await InsertAsync(SampleFactory.Create<LogImportDto>());
    }

    private async Task InsertAsync<TDto>(TDto entity) where TDto : class
    {
        var repository = new SqlRepository<TDto>(Provider);
        await repository.CreateAsync(entity);
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
}
