using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// List Mode ingestion (FS-CRAWL-002 §10.2, criterion #43): user-supplied URLs
/// become crawl seeds with discovery source <c>list</c> at depth 0. Pipeline:
/// per-entry validation → on-the-fly rewriting (§10.4) → canonicalization
/// (§5.2) → seen-set deduplication → frontier enqueue + url_records persistence.
/// With <c>allowDiscovery=false</c> the audit is list-only (depth ceiling 0,
/// link discovery and sitemap expansion disabled); with <c>true</c> it is
/// list-bounded (only on-list discoveries enqueue — see <see cref="ListDiscoveryGate"/>).
/// Persistence uses provider-neutral SQL through the locked <see cref="IDbProvider"/>;
/// no endpoint other than the configured database is ever contacted. Seeding is
/// idempotent and race-safe (G-MATRIX-SEED-RACE seeder-side fix): a concurrent
/// inserter of the same canonical URL is counted as a duplicate, never fails
/// the audit.
/// </summary>
public sealed class ListModeIngestor : IListModeIngestor
{
    private readonly IDbProvider _provider;
    private readonly IUrlCanonicalizer _canonicalizer;
    private readonly IUrlRewriteEngine _rewriteEngine;
    private readonly IPriorityFrontier _frontier;
    private readonly ListModeOptions _options;
    private readonly RewriteRuleGuard _rewriteGuard;

    /// <summary>Creates the ingestor over the locked contracts.</summary>
    /// <param name="provider">Configured database provider (embedded or user-owned external).</param>
    /// <param name="canonicalizer">Locked URL canonicalizer (§5.2).</param>
    /// <param name="rewriteEngine">Locked rewrite engine (§10.4).</param>
    /// <param name="frontier">Locked priority frontier receiving the list seeds.</param>
    /// <param name="options">List Mode options; null uses the list-only baseline.</param>
    public ListModeIngestor(
        IDbProvider provider,
        IUrlCanonicalizer canonicalizer,
        IUrlRewriteEngine rewriteEngine,
        IPriorityFrontier frontier,
        ListModeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        ArgumentNullException.ThrowIfNull(rewriteEngine);
        ArgumentNullException.ThrowIfNull(frontier);

        _provider = provider;
        _canonicalizer = canonicalizer;
        _rewriteEngine = rewriteEngine;
        _frontier = frontier;
        _options = options ?? ListModeOptions.Default;
        _rewriteGuard = new RewriteRuleGuard(_options.RewriteRules ?? []);
    }

    /// <inheritdoc/>
    public async Task<ListIngestionResult> IngestAsync(
        long auditId,
        IReadOnlyList<string> urls,
        bool allowDiscovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await ReadExistingCanonicalUrlsAsync(auditId, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        var accepted = new List<(string CanonicalUrl, Uri Uri)>();
        var duplicateCount = 0;
        var invalidCount = 0;

        foreach (var raw in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (raw is null || !ListInputParser.TryValidateUrl(raw.Trim(), out _))
            {
                invalidCount++;
                continue;
            }

            // §10.4: rewriting applies during canonicalization, before seen-set keying.
            var rewritten = _rewriteGuard.Apply(raw.Trim(), _rewriteEngine);
            var canonical = _canonicalizer.Canonicalize(rewritten);
            if (!seen.Add(canonical))
            {
                duplicateCount++;
                continue;
            }

            if (!Uri.TryCreate(canonical, UriKind.Absolute, out var uri))
            {
                invalidCount++;
                continue;
            }

            accepted.Add((canonical, uri));
        }

        var persisted = accepted;
        var raceDuplicates = 0;
        if (accepted.Count > 0)
        {
            (persisted, raceDuplicates) = await PersistSeedsAsync(auditId, accepted, cancellationToken).ConfigureAwait(false);

            foreach (var (canonical, uri) in persisted)
            {
                await _frontier.EnqueueAsync(
                    new FrontierEntry(canonical, uri.Host, Depth: 0, Priority: 1.0, DiscoverySource.List),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new ListIngestionResult(persisted.Count, duplicateCount + raceDuplicates, invalidCount);
    }

    /// <summary>
    /// Resolves the per-audit discovery policy from the persisted list seeds.
    /// List-only forces depth ceiling 0 and disables discovery; list-bounded
    /// enables discovery restricted to the canonical list set.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="allowDiscovery">Whether bounded discovery is enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved policy consumed by <see cref="ListDiscoveryGate"/>.</returns>
    public async Task<ListModePolicy> ResolvePolicyAsync(
        long auditId,
        bool allowDiscovery,
        CancellationToken cancellationToken = default)
    {
        var listUrls = await ReadListCanonicalUrlsAsync(auditId, cancellationToken).ConfigureAwait(false);
        var mode = allowDiscovery ? ListDiscoveryMode.ListBounded : ListDiscoveryMode.ListOnly;
        return new ListModePolicy(
            mode,
            DepthCeiling: allowDiscovery ? Math.Max(0, _options.BoundedDepthCeiling) : 0,
            DiscoveryEnabled: allowDiscovery,
            AllowedCanonicalUrls: listUrls);
    }

    private async Task<HashSet<string>> ReadExistingCanonicalUrlsAsync(long auditId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `canonical_url` FROM `url_records` WHERE `audit_id` = @auditId";
        AddParameter(command, "@auditId", auditId);

        var urls = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            urls.Add(reader.GetString(0));
        }

        return urls;
    }

    private async Task<IReadOnlySet<string>> ReadListCanonicalUrlsAsync(long auditId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `canonical_url` FROM `url_records` WHERE `audit_id` = @auditId AND `discovery_source` = @listSource";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@listSource", (int)DiscoverySource.List);

        var urls = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            urls.Add(reader.GetString(0));
        }

        return urls;
    }

    /// <summary>
    /// Persists list seeds idempotently (G-MATRIX-SEED-RACE seeder-side fix):
    /// each row is re-checked inside the write transaction before insert, and
    /// a unique-constraint failure (a concurrent inserter won the race, e.g.
    /// the matrix auto-seed racing the crawl's own seed insert) is re-checked
    /// and counted as a duplicate instead of failing the audit. Write
    /// contention that invalidates the transaction snapshot (WAL busy) retries
    /// the whole batch on a fresh transaction with a bounded attempt count;
    /// the re-check then sees the concurrent inserter's committed rows. Only
    /// actually persisted rows are returned for frontier enqueue.
    /// Provider-neutral: no dialect-specific upsert syntax is used.
    /// </summary>
    private async Task<(List<(string CanonicalUrl, Uri Uri)> Persisted, int RaceDuplicates)> PersistSeedsAsync(
        long auditId,
        List<(string CanonicalUrl, Uri Uri)> accepted,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        DbException? lastContention = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            var persisted = new List<(string CanonicalUrl, Uri Uri)>(accepted.Count);
            var raceDuplicates = 0;
            var contended = false;

            foreach (var (canonical, uri) in accepted)
            {
                if (await SeedExistsAsync(connection, transaction, auditId, canonical, cancellationToken).ConfigureAwait(false))
                {
                    raceDuplicates++;
                    continue;
                }

                try
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        "INSERT INTO `url_records` " +
                        "(`audit_id`, `canonical_url`, `host`, `path`, `depth`, `discovery_source`, `status_class`, `robots_state`, `indexability_flags`, `is_indexable`) " +
                        "VALUES (@auditId, @canonicalUrl, @host, @path, 0, @discoverySource, @statusClass, @robotsState, @indexabilityFlags, @isIndexable)";
                    AddParameter(command, "@auditId", auditId);
                    AddParameter(command, "@canonicalUrl", canonical);
                    AddParameter(command, "@host", uri.Host);
                    AddParameter(command, "@path", uri.AbsolutePath);
                    AddParameter(command, "@discoverySource", (int)DiscoverySource.List);
                    AddParameter(command, "@statusClass", (int)CrawlStatusClass.NotCrawled);
                    AddParameter(command, "@robotsState", (int)RobotsState.Unknown);
                    AddParameter(command, "@indexabilityFlags", (int)IndexabilityFlags.None);
                    AddParameter(command, "@isIndexable", true);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    persisted.Add((canonical, uri));
                }
                catch (DbException ex)
                {
                    // A concurrent inserter committed the same canonical URL
                    // between the re-check and the insert: count as duplicate.
                    bool exists;
                    try
                    {
                        exists = await SeedExistsAsync(connection, transaction, auditId, canonical, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DbException)
                    {
                        exists = false;
                    }

                    if (exists)
                    {
                        raceDuplicates++;
                        continue;
                    }

                    // Snapshot/write contention: retry the batch on a fresh
                    // transaction (disposal rolls this one back).
                    lastContention = ex;
                    contended = true;
                    break;
                }
            }

            if (contended)
            {
                continue;
            }

            transaction.Commit();
            return (persisted, raceDuplicates);
        }

        throw new InvalidOperationException(
            $"List seeding for audit {auditId} remained contended after {maxAttempts} attempts.", lastContention);
    }

    private static async Task<bool> SeedExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        string canonicalUrl,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM `url_records` WHERE `audit_id` = @auditId AND `canonical_url` = @canonicalUrl";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@canonicalUrl", canonicalUrl);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
