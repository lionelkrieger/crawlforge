using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// Orphan-detection rule options.
/// </summary>
/// <param name="ExcludeEntryPoints">
/// When true (default), seed/list entry points are not reported as orphans:
/// they legitimately enter the crawl without in-links (the link-graph read
/// model separates entry points from true orphans — see
/// Audit/LinkGraph/LinkGraphAnalyzer). Sitemap-discovered URLs are always
/// eligible: the fixture orphan trap is a sitemap-only page.
/// </param>
public sealed record OrphanDetectionOptions(bool ExcludeEntryPoints = true);

/// <summary>
/// Orphan identification with findings (CAP-B-048, CAP-E-122; schema catalog:
/// link_edges "supports orphan and graph analysis"). A URL is an orphan when
/// it has zero internal in-links from another URL (self-loops excluded — the
/// same definition the accepted link-graph analyzer streams). Sitemap
/// membership rides in the evidence so consumers can distinguish sitemap-only
/// orphans (the fixture trap) from other unreachable pages.
/// </summary>
/// <remarks>
/// Two bounded streaming passes over the persisted graph (edges, then URLs)
/// with compact identifier-keyed state only — no row materialization (scale
/// invariant, precedence rule 7). Pure reader: emits <see cref="FindingDto"/>
/// drafts (Id 0); persistence goes through <see cref="CrawlFindingRecorder"/>
/// or the engine's finding writes (WP-09-11 wiring). Thread-safe; safe as a
/// singleton and under concurrent analyzer execution.
/// </remarks>
public sealed class OrphanFindingAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbProvider _provider;

    /// <summary>Creates the analyzer over the configured provider.</summary>
    /// <param name="provider">The configured database provider.</param>
    public OrphanFindingAnalyzer(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Streams the audit's link graph and URL records, emitting one
    /// <c>links.orphan</c> finding per orphan URL.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="sitemapCanonicalUrls">
    /// Canonical sitemap URL set (from sitemap ingestion), when available;
    /// membership is stamped into the finding evidence.
    /// </param>
    /// <param name="options">Rule options; defaults when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Finding drafts in url_records id order.</returns>
    public async Task<IReadOnlyList<FindingDto>> AnalyzeAsync(
        long auditId,
        IReadOnlyCollection<string>? sitemapCanonicalUrls = null,
        OrphanDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? new OrphanDetectionOptions();
        var sitemapSet = sitemapCanonicalUrls is null
            ? null
            : new HashSet<string>(sitemapCanonicalUrls, StringComparer.OrdinalIgnoreCase);

        // Pass 1: internal in-link targets (self-loops excluded).
        var linkedTargets = new HashSet<long>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT source_url_id, target_url_id FROM link_edges " +
                "WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NOT NULL ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var source = reader.GetInt64(0);
                var target = reader.GetInt64(1);
                if (target != source)
                {
                    linkedTargets.Add(target);
                }
            }
        }

        // Pass 2: stream URLs; unlinked non-entry-point URLs are orphans.
        var findings = new List<FindingDto>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, canonical_url, discovery_source, depth FROM url_records WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var urlId = reader.GetInt64(0);
                if (linkedTargets.Contains(urlId))
                {
                    continue;
                }

                var canonicalUrl = reader.GetString(1);
                var discoverySource = (DiscoverySource)reader.GetInt32(2);
                var depth = reader.GetInt32(3);
                if (effective.ExcludeEntryPoints && discoverySource is DiscoverySource.Seed or DiscoverySource.List)
                {
                    continue;
                }

                var inSitemap = sitemapSet is not null && sitemapSet.Contains(canonicalUrl);
                findings.Add(new FindingDto(
                    Id: 0,
                    AuditId: auditId,
                    RuleKey: LinkRuleKeys.Orphan,
                    Category: AuditCategory.Indexability,
                    Severity: FindingSeverity.Warning,
                    UrlId: urlId,
                    EvidenceJson: JsonSerializer.Serialize(new
                    {
                        url = canonicalUrl,
                        urlId,
                        discoverySource = discoverySource.ToString(),
                        depth,
                        inSitemap,
                        internalInlinkCount = 0,
                    }, EvidenceJson),
                    RecommendationKey: null,
                    IsSuppressed: false));
            }
        }

        return findings;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
