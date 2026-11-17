using System.Data.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Persistence;

/// <summary>
/// Database provider abstraction (FS-CRAWL-001 §2.4). Both the embedded
/// file-based provider and the optional user-owned external provider implement
/// every logical schema object (provider agnosticism, precedence rule 6).
/// Connections target only the local embedded database or the user-configured
/// external server — never a vendor service.
/// </summary>
public interface IDbProvider
{
    /// <summary>Provider kind.</summary>
    DatabaseProviderKind Kind { get; }

    /// <summary>Provider dialect identifier recorded in the migration ledger.</summary>
    string Dialect { get; }

    /// <summary>Opens a connection to the configured database.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection owned by the caller.</returns>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Tests reachability, authentication, and migration compatibility.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test verdict.</returns>
    Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Database connection test verdict
/// (REST: POST /api/settings/database/test).
/// </summary>
/// <param name="IsReachable">Whether the server is reachable.</param>
/// <param name="AuthenticationOk">Whether authentication succeeded.</param>
/// <param name="MigrationCompatible">Whether the provider supports all migrations.</param>
/// <param name="Message">Human-readable verdict detail.</param>
public sealed record DbConnectionTestResult(
    bool IsReachable,
    bool AuthenticationOk,
    bool MigrationCompatible,
    string? Message);

/// <summary>
/// Versioned, provider-agnostic schema migrator (FS-CRAWL-001 §9.4): creates
/// all logical objects, then seeds deterministic first-run defaults with zero
/// network calls. The migration ledger is idempotent per provider.
/// </summary>
public interface ISchemaMigrator
{
    /// <summary>Returns the current schema version of the configured provider.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current schema version.</returns>
    Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies all pending migrations idempotently and seeds first-run defaults.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the schema is up to date.</returns>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists applied migrations from the ledger.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Applied migration rows.</returns>
    Task<IReadOnlyList<SchemaMigrationDto>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default);
}
