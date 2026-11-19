using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Cross-URL duplicate-content aggregation pass (FS-CRAWL-001 §7.4
/// content-quality row: "near-duplicate bodies"; §7.5 shingle similarity;
/// §7.6 aggregation over indexed views; CAP-B-062/CAP-F-139 exact duplicate
/// content, CAP-B-063/CAP-F-140 near-duplicate content). This is the
/// pipeline-ready adapter for the previously unwired
/// <see cref="ShingleNearDuplicateDetector"/> (UNDERBUILD G23): it converts
/// detector output into persisted-shape findings and adds exact-duplicate
/// grouping.
/// </summary>
/// <remarks>
/// <para>
/// D14 site-pass pattern: the engine invokes <see cref="AnalyzeSiteAsync"/>
/// once after the per-URL passes. Two signals over STORED bodies only
/// (content-store artifacts, never re-fetched — §7.2/§9.6):
/// </para>
/// <list type="bullet">
/// <item><b>Exact duplicates</b>: SHA-256 over the whitespace-normalized
/// visible text (scoped when a scope is supplied, FS-CRAWL-002 §4.3). One
/// <c>content.duplicate-content</c> finding per hash group of ≥2 URLs.</item>
/// <item><b>Near duplicates</b>: <see cref="IShingleNearDuplicateDetector"/>
/// Jaccard similarity over token shingles. One
/// <c>content.near-duplicate-content</c> finding per pair at or above the
/// threshold, EXCLUDING pairs already covered by an exact-duplicate group
/// (identical text is reported once, as exact).</item>
/// </list>
/// <para>
/// Scale invariance (§7.6): both passes page through the artifact index
/// (200 rows/page) and are bounded by <see cref="ShingleOptions.MaxUrls"/>;
/// near-duplicate pair findings are bounded by
/// <see cref="DuplicateContentOptions.MaxReportedPairs"/> and group members by
/// <see cref="DuplicateContentOptions.MaxGroupMembers"/> (the full count always
/// rides <c>urlCount</c>). Deterministic output: groups ordered by hash,
/// pairs by (urlIdA, urlIdB). Thread-safe: no mutable instance state.
/// </para>
/// </remarks>
public sealed class DuplicateContentAnalyzer
{
    private const int PageSize = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ShingleNearDuplicateDetector _nearDuplicateDetector;
    private readonly IReadRepository<ContentArtifactDto> _artifacts;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IContentStore _contentStore;
    private readonly ITolerantDomParser _parser;
    private readonly IContentScopeResolver? _scopeResolver;
    private readonly DuplicateContentOptions _options;
    private readonly ShingleOptions _shingleOptions;

    /// <summary>Creates the site pass over the locked persistence contracts.</summary>
    /// <param name="nearDuplicateDetector">
    /// Shingle near-duplicate detector (G23 component). The concrete family
    /// type is required for the scope-aware overload: with a content scope,
    /// BOTH signals must consume the same scoped text basis (FS-CRAWL-002 §4.3).
    /// </param>
    /// <param name="artifacts">Content-artifact index (audit-scoped enumeration).</param>
    /// <param name="urls">URL-record index (urlId → canonical URL for evidence).</param>
    /// <param name="contentStore">Compressed body store.</param>
    /// <param name="parser">Tolerant DOM parser for stored bodies.</param>
    /// <param name="scopeResolver">Optional scope resolver; with a scope, both signals use scoped text.</param>
    /// <param name="options">Duplicate-content pass options.</param>
    /// <param name="shingleOptions">Shingle bounds shared with the detector (MaxUrls/MaxCharacters).</param>
    public DuplicateContentAnalyzer(
        ShingleNearDuplicateDetector nearDuplicateDetector,
        IReadRepository<ContentArtifactDto> artifacts,
        IReadRepository<UrlRecordDto> urls,
        IContentStore contentStore,
        ITolerantDomParser parser,
        IContentScopeResolver? scopeResolver = null,
        DuplicateContentOptions? options = null,
        ShingleOptions? shingleOptions = null)
    {
        ArgumentNullException.ThrowIfNull(nearDuplicateDetector);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(contentStore);
        ArgumentNullException.ThrowIfNull(parser);
        _nearDuplicateDetector = nearDuplicateDetector;
        _artifacts = artifacts;
        _urls = urls;
        _contentStore = contentStore;
        _parser = parser;
        _scopeResolver = scopeResolver;
        _options = options ?? DuplicateContentOptions.Default;
        _shingleOptions = shingleOptions ?? ShingleOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxReportedPairs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxGroupMembers, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.NearDuplicateSimilarityThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.NearDuplicateSimilarityThreshold, 1.0);
    }

    /// <summary>
    /// Runs the duplicate-content aggregation over one audit's stored bodies.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="scope">Content scope, or null for baseline full visible text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exact-duplicate group findings followed by near-duplicate pair findings.</returns>
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeSiteAsync(
        long auditId,
        ContentScopeDefinition? scope = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Exact duplicates: hash the normalized visible text of stored bodies.
        var hashGroups = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var seenUrlIds = new HashSet<long>();
        var offset = 0;
        var consumed = 0;
        while (consumed < _shingleOptions.MaxUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _artifacts.ListAsync(new ListQuery(
                Offset: offset,
                Limit: PageSize,
                Filters: new Dictionary<string, string>
                {
                    ["auditId"] = auditId.ToString(CultureInfo.InvariantCulture),
                    ["kind"] = ((int)ContentArtifactKind.Body).ToString(CultureInfo.InvariantCulture),
                },
                SortBy: "urlId"), cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var artifact in page.Items)
            {
                if (consumed >= _shingleOptions.MaxUrls)
                {
                    break;
                }

                if (!seenUrlIds.Add(artifact.UrlId))
                {
                    continue;
                }

                consumed++;
                var text = await ReadVisibleTextAsync(artifact, scope, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Empty bodies are thin content, not duplicate content.
                    continue;
                }

                var hash = HashText(text);
                if (!hashGroups.TryGetValue(hash, out var group))
                {
                    group = [];
                    hashGroups[hash] = group;
                }

                group.Add(artifact.UrlId);
            }

            offset += page.Items.Count;
            if (offset >= page.TotalCount)
            {
                break;
            }
        }

        // urlId → canonical URL cache for evidence (bounded by touched URLs).
        var urlCache = new Dictionary<long, string>();
        var findings = new List<FindingDraft>();

        foreach (var (hash, group) in hashGroups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Count < 2)
            {
                continue;
            }

            var ordered = group.OrderBy(id => id).ToArray();
            var members = new List<object>(_options.MaxGroupMembers);
            foreach (var urlId in ordered.Take(_options.MaxGroupMembers))
            {
                members.Add(new { urlId, url = await ResolveUrlAsync(urlId, urlCache, cancellationToken).ConfigureAwait(false) });
            }

            var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["contentHash"] = hash,
                ["urlCount"] = ordered.Length,
                ["membersTruncated"] = ordered.Length > members.Count,
                ["urls"] = members,
            };
            findings.Add(new FindingDraft(
                ContentQualityRuleKeys.DuplicateContent,
                AuditCategory.Content,
                FindingSeverity.Warning,
                UrlId: null,
                JsonSerializer.Serialize(evidence, JsonOptions),
                RecommendationKeys.ForRule(ContentQualityRuleKeys.DuplicateContent)));
        }

        // 2. Near duplicates through the shingle detector (G23), excluding
        // pairs already reported as exact duplicates.
        var pairs = await _nearDuplicateDetector.FindNearDuplicatesAsync(
            auditId, _options.NearDuplicateSimilarityThreshold, scope, cancellationToken).ConfigureAwait(false);

        var exactMembership = new Dictionary<long, string>();
        foreach (var (hash, group) in hashGroups)
        {
            if (group.Count >= 2)
            {
                foreach (var urlId in group)
                {
                    exactMembership[urlId] = hash;
                }
            }
        }

        var reported = 0;
        foreach (var pair in pairs)
        {
            if (reported >= _options.MaxReportedPairs)
            {
                break;
            }

            if (exactMembership.TryGetValue(pair.UrlIdA, out var hashA)
                && exactMembership.TryGetValue(pair.UrlIdB, out var hashB)
                && string.Equals(hashA, hashB, StringComparison.Ordinal))
            {
                continue;
            }

            reported++;
            var evidence = new
            {
                urlIdA = pair.UrlIdA,
                urlA = await ResolveUrlAsync(pair.UrlIdA, urlCache, cancellationToken).ConfigureAwait(false),
                urlIdB = pair.UrlIdB,
                urlB = await ResolveUrlAsync(pair.UrlIdB, urlCache, cancellationToken).ConfigureAwait(false),
                similarity = pair.Similarity,
                threshold = _options.NearDuplicateSimilarityThreshold,
            };
            findings.Add(new FindingDraft(
                ContentQualityRuleKeys.NearDuplicateContent,
                AuditCategory.Content,
                FindingSeverity.Warning,
                UrlId: null,
                JsonSerializer.Serialize(evidence, JsonOptions),
                RecommendationKeys.ForRule(ContentQualityRuleKeys.NearDuplicateContent)));
        }

        return findings;
    }

    private async Task<string> ResolveUrlAsync(long urlId, Dictionary<long, string> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(urlId, out var cached))
        {
            return cached;
        }

        var record = await _urls.GetByIdAsync(urlId, cancellationToken).ConfigureAwait(false);
        var url = record?.CanonicalUrl ?? string.Empty;
        cache[urlId] = url;
        return url;
    }

    private async Task<string> ReadVisibleTextAsync(ContentArtifactDto artifact, ContentScopeDefinition? scope, CancellationToken cancellationToken)
    {
        await using var stream = await _contentStore.OpenReadAsync(artifact.StorageKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var document = _parser.Parse(html, artifact.StorageKey);
        if (scope is not null && _scopeResolver is not null)
        {
            return _scopeResolver.Resolve(document, scope).Text;
        }

        return VisibleTextExtractor.ExtractVisibleText(document);
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
