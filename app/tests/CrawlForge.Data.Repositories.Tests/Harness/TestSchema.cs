using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Migrations;

namespace CrawlForge.Data.Repositories.Tests.Harness;

/// <summary>
/// Harness schema application, re-based on the REAL schema authority
/// (WP-01-04 reopen): the production <see cref="SchemaMigrator"/> applies the
/// accepted <c>SchemaCatalog</c> migrations and first-run seeding. The previous
/// EntityMap-derived DDL was self-consistent with the mapping layer but could
/// diverge from the catalog — that divergence hid the column-name defects this
/// suite now guards against. Test DDL is never generated from EntityMap again.
/// </summary>
public static class TestSchema
{
    /// <summary>Applies the real migrated schema (all versions + first-run seed) to a fresh database.</summary>
    /// <param name="provider">The provider to migrate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task ApplyAsync(IDbProvider provider, CancellationToken cancellationToken = default)
    {
        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync(cancellationToken);
    }
}
