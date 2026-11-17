using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.NGrams;

/// <summary>
/// N-gram rule host (FS-CRAWL-002 §5): the per-URL pass extracts the four zone
/// texts (title never scoped; body and headings on the Scoped Visible Text
/// Tree — criterion #38; inbound-anchor from recorded internal link edges),
/// caches H1/canonical page facts, and persists bounded frequencies. The
/// site pass (<see cref="RunSitePassAsync"/>) materializes summaries and emits
/// <c>content.keyword-cannibalization</c> clusters (Warning, downgraded to
/// Notice on a uniform declared canonical target) and <c>content.topical-gap</c>
/// findings (anchor demand without on-page supply), plus the ngram-top,
/// cannibalization-clusters, and topical-gaps metric contributions.
/// </summary>
public sealed class NGramRuleAnalyzer : IAuditRuleAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly NGramAnalyzer _analyzer;
    private readonly IStopWordFilter _stopWords;
    private readonly IDbProvider _provider;
    private readonly IContentScopeResolver? _scopeResolver;
    private readonly NGramAnalyzerOptions _options;

    /// <summary>Creates the rule host.</summary>
    /// <param name="analyzer">The n-gram analyzer (persistence + primaries).</param>
    /// <param name="stopWords">Stop-word filter for anchor-text scanning.</param>
    /// <param name="provider">Database provider for link-edge and URL reads.</param>
    /// <param name="scopeResolver">Scoped-text resolver; null uses context scoped text only.</param>
    /// <param name="options">Configuration and bounds; null uses defaults.</param>
    public NGramRuleAnalyzer(
        NGramAnalyzer analyzer,
        IStopWordFilter stopWords,
        IDbProvider provider,
        IContentScopeResolver? scopeResolver = null,
        NGramAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(stopWords);
        ArgumentNullException.ThrowIfNull(provider);
        _analyzer = analyzer;
        _stopWords = stopWords;
        _provider = provider;
        _scopeResolver = scopeResolver;
        _options = options ?? NGramAnalyzerOptions.Default;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys { get; } =
        [FindingRuleKeys.ContentKeywordCannibalization, FindingRuleKeys.ContentTopicalGap];

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        await AnalyzePageAsync(context, cancellationToken).ConfigureAwait(false);
        return [];
    }

    /// <summary>
    /// Per-URL pass with the rich result (rows + bound counters). WAF-blocked
    /// and non-HTML records are excluded (§5.1). Findings are site-level and
    /// emitted by <see cref="RunSitePassAsync"/>.
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-URL analysis, or null when the record is excluded.</returns>
    public async Task<NGramUrlAnalysis?> AnalyzePageAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var url = context.Url;
        if (url.StatusClass != CrawlStatusClass.Success)
        {
            return null;
        }

        if (url.ContentType is not null && !url.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var document = context.Document ?? context.RenderedDocument;
        if (document is null)
        {
            return null;
        }

        var scopedText = ResolveScopedText(context, document);
        var zoneTexts = new Dictionary<TextZone, string>
        {
            [TextZone.Title] = ZoneTextExtractor.ExtractTitle(document),
            [TextZone.Headings] = ZoneTextExtractor.ExtractHeadings(document, scopedText),
            [TextZone.Body] = scopedText ?? ZoneTextExtractor.ExtractVisibleText(document),
            [TextZone.AnchorText] = await _analyzer.GetInboundAnchorTextAsync(context.AuditId, url.Id, cancellationToken).ConfigureAwait(false),
        };
        var language = ZoneTextExtractor.ExtractLanguage(document) ?? _options.PreferredLanguage;

        _analyzer.CachePageFacts(context.AuditId, url.Id, document, scopedText);
        var input = new NgramUrlInput(context.AuditId, url.Id, language, zoneTexts, [1, 2, 3]);
        return await _analyzer.AnalyzeUrlCoreAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Site-wide pass (§5.1 step 5): materializes ngram_site_summaries, then
    /// detects cannibalization clusters and anchor-demand topical gaps over
    /// indexable URLs, emitting findings and metric contributions.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summaries, clusters, gaps, findings, metrics, and counters.</returns>
    public async Task<NgramSitePassResult> RunSitePassAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var (summaries, counters) = await _analyzer.SummarizeSiteCoreAsync(auditId, cancellationToken).ConfigureAwait(false);
        var primaries = await _analyzer.ComputePrimaryTermsAsync(auditId, cancellationToken).ConfigureAwait(false);
        var summaryDf = summaries.ToDictionary(s => (s.Zone, s.Order, s.Term), s => s.DocumentFrequency);

        var clusters = BuildClusters(primaries, summaryDf);
        var gaps = await BuildGapsAsync(auditId, summaries, cancellationToken).ConfigureAwait(false);

        var findings = new List<FindingDraft>();
        foreach (var cluster in clusters)
        {
            findings.Add(new FindingDraft(
                FindingRuleKeys.ContentKeywordCannibalization,
                AuditCategory.Content,
                cluster.SameCanonicalTarget ? FindingSeverity.Notice : FindingSeverity.Warning,
                null,
                JsonSerializer.Serialize(new
                {
                    term = cluster.Term,
                    order = cluster.Order,
                    zone = ZoneKey(cluster.Zone),
                    siteDf = cluster.SiteDocumentFrequency,
                    sameCanonicalTarget = cluster.SameCanonicalTarget,
                    members = cluster.Members.Select(m => new
                    {
                        urlId = m.UrlId,
                        url = m.CanonicalUrl,
                        tf = m.TermFrequency,
                        canonicalTarget = m.DeclaredCanonicalTarget,
                    }),
                }, EvidenceJson),
                null));
        }

        foreach (var gap in gaps)
        {
            findings.Add(new FindingDraft(
                FindingRuleKeys.ContentTopicalGap,
                AuditCategory.Content,
                FindingSeverity.Notice,
                null,
                JsonSerializer.Serialize(new
                {
                    term = gap.Term,
                    order = gap.Order,
                    anchorDf = gap.AnchorDocumentFrequency,
                    threshold = gap.Threshold,
                    sampleSourceUrls = gap.SampleSourceUrls,
                }, EvidenceJson),
                null));
        }

        var metrics = new List<MetricContribution>();
        foreach (var group in summaries.GroupBy(s => (s.Zone, s.Order)))
        {
            metrics.Add(new MetricContribution(
                MetricKeys.NgramTop,
                JsonSerializer.Serialize(new { zone = ZoneKey(group.Key.Zone), order = group.Key.Order }, EvidenceJson),
                group.Count()));
        }

        metrics.Add(new MetricContribution(MetricKeys.CannibalizationClusters, "{}", clusters.Count));
        metrics.Add(new MetricContribution(MetricKeys.TopicalGaps, "{}", gaps.Count));

        return new NgramSitePassResult(summaries, clusters, gaps, findings, metrics, counters);
    }

    private List<NgramCannibalizationCluster> BuildClusters(
        IReadOnlyList<UrlPrimaryTerms> primaries,
        Dictionary<(TextZone Zone, int Order, string Term), long> summaryDf)
    {
        var groups = new Dictionary<(TextZone Zone, int Order, string Term), List<(UrlPrimaryTerms Url, PrimaryTerm Term)>>();
        foreach (var url in primaries)
        {
            if (_options.IndexableOnly && !url.IsIndexable)
            {
                continue;
            }

            foreach (var term in url.Terms)
            {
                var key = (term.Zone, term.Order, term.Term);
                if (!groups.TryGetValue(key, out var members))
                {
                    members = [];
                    groups[key] = members;
                }

                members.Add((url, term));
            }
        }

        var clusters = new List<NgramCannibalizationCluster>();
        foreach (var (key, members) in groups)
        {
            if (members.Count < _options.MinClusterSize)
            {
                continue;
            }

            var ordered = members.OrderBy(m => m.Url.UrlId).ToList();
            var targets = ordered.Select(m => m.Url.DeclaredCanonicalTarget).Distinct(StringComparer.Ordinal).ToList();
            var sameCanonical = targets.Count == 1 && targets[0] is not null;
            clusters.Add(new NgramCannibalizationCluster(
                key.Zone,
                key.Order,
                key.Term,
                ordered.Select(m => new NgramClusterMember(m.Url.UrlId, m.Url.CanonicalUrl, m.Term.TermFrequency, m.Url.DeclaredCanonicalTarget)).ToList(),
                summaryDf.TryGetValue(key, out var df) ? df : 0,
                sameCanonical));
        }

        return clusters.OrderBy(c => c.Zone).ThenBy(c => c.Order).ThenBy(c => c.Term, StringComparer.Ordinal).ToList();
    }

    private async Task<List<NgramTopicalGap>> BuildGapsAsync(
        long auditId,
        IReadOnlyList<NgramSiteSummaryDto> summaries,
        CancellationToken cancellationToken)
    {
        if (!_options.TopicalGapEnabled)
        {
            return [];
        }

        // Zero-DF check uses raw frequencies (retention bounds may drop DF=1
        // title/headings terms from the materialized summaries).
        var onPageKeys = new HashSet<(int Order, string Term)>();
        using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT `ngram_order`, `term` FROM `ngram_frequencies` " +
                "WHERE `audit_id` = @auditId AND `zone` IN (0, 1)";
            AddParameter(command, "@auditId", auditId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                onPageKeys.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        var candidates = summaries
            .Where(s => s.Zone == TextZone.AnchorText && s.DocumentFrequency >= _options.MinAnchorDf)
            .Where(s => !onPageKeys.Contains((s.Order, s.Term)))
            .OrderByDescending(s => s.DocumentFrequency)
            .ThenBy(s => s.Term, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var candidateKeys = candidates.Select(c => (c.Order, c.Term)).ToHashSet();
        var samples = await CollectAnchorSamplesAsync(auditId, candidateKeys, cancellationToken).ConfigureAwait(false);

        return candidates.Select(c => new NgramTopicalGap(
            c.Term,
            c.Order,
            c.DocumentFrequency,
            _options.MinAnchorDf,
            samples.TryGetValue((c.Order, c.Term), out var urls) ? urls : [])).ToList();
    }

    private async Task<Dictionary<(int Order, string Term), List<string>>> CollectAnchorSamplesAsync(
        long auditId,
        HashSet<(int Order, string Term)> candidateKeys,
        CancellationToken cancellationToken)
    {
        var urlMap = new Dictionary<long, string>();
        using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT `id`, `canonical_url` FROM `url_records` WHERE `audit_id` = @auditId";
            AddParameter(command, "@auditId", auditId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                urlMap[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        var samples = new Dictionary<(int, string), List<string>>();
        var seen = new Dictionary<(int, string), HashSet<long>>();
        using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT `source_url_id`, `anchor_text` FROM `link_edges` " +
                "WHERE `audit_id` = @auditId AND `is_internal` = 1 AND `anchor_text` IS NOT NULL";
            AddParameter(command, "@auditId", auditId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sourceUrlId = reader.GetInt64(0);
                var anchorText = reader.GetString(1);
                var sentences = NGramTokenizer.TokenizeSentences(anchorText, _options.MinTokenLength, _options.ExcludeNumericTokens);
                var filtered = sentences.Select(s => _stopWords.RemoveStopWords(s, _options.PreferredLanguage)).ToArray();
                var ngrams = NGramTokenizer.BuildNgrams(filtered, [1, 2, 3]);
                foreach (var (order, counts) in ngrams)
                {
                    foreach (var term in counts.Keys)
                    {
                        var key = (order, term);
                        if (!candidateKeys.Contains(key) || !urlMap.TryGetValue(sourceUrlId, out var sourceUrl))
                        {
                            continue;
                        }

                        if (!seen.TryGetValue(key, out var urlIds))
                        {
                            urlIds = [];
                            seen[key] = urlIds;
                            samples[key] = [];
                        }

                        if (urlIds.Add(sourceUrlId) && samples[key].Count < 5)
                        {
                            samples[key].Add(sourceUrl);
                        }
                    }
                }
            }
        }

        return samples;
    }

    private string? ResolveScopedText(PageAuditContext context, DomDocument document)
    {
        if (context.ScopedText is not null)
        {
            return context.ScopedText.Text;
        }

        if (context.Configuration.ContentScope is { } scope && _scopeResolver is not null)
        {
            return _scopeResolver.Resolve(document, scope).Text;
        }

        return null;
    }

    private static string ZoneKey(TextZone zone) => zone switch
    {
        TextZone.Title => "title",
        TextZone.Headings => "headings",
        TextZone.Body => "body",
        TextZone.AnchorText => "inbound-anchor",
        _ => zone.ToString().ToLowerInvariant(),
    };

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
