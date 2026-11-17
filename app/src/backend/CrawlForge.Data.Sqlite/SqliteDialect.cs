namespace CrawlForge.Data.Sqlite;

/// <summary>
/// Dialect identity of the embedded provider, recorded in the provider-agnostic
/// migration ledger (schema catalog: <c>schema_migrations.provider dialect</c>).
/// </summary>
public static class SqliteDialect
{
    /// <summary>Dialect identifier written to and matched against the migration ledger.</summary>
    public const string Name = "sqlite";
}
