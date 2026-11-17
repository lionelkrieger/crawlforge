using System.Collections.Concurrent;
using System.Data.Common;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.NGrams;

/// <summary>
/// One primary term of a URL (FS-CRAWL-002 §5.4): the highest-density 2-gram
/// or 3-gram in the title zone (weight 2.0) or in H1 text (weight 1.5, from
/// the headings source). Density = TF × log(1 + N/DF) × zone weight.
/// </summary>
/// <param name="Zone">Persisted zone (Title, or Headings for H1-derived terms).</param>
/// <param name="Order">N-gram order (2 or 3).</param>
/// <param name="Term">Normalized term.</param>
/// <param name="TermFrequency">Per-URL TF in the zone/order.</param>
/// <param name="Density">Zone-weighted topical density.</param>
public sealed record PrimaryTerm(TextZone Zone, int Order, string Term, double TermFrequency, double Density);

/// <summary>
/// Per-URL primary terms plus the URL facts needed by cannibalization
/// detection (indexability, canonical URL, declared canonical target).
/// </summary>
/// <param name="UrlId">URL record identifier.</param>
/// <param name="CanonicalUrl">The URL's canonical URL.</param>
/// <param name="IsIndexable">Whether the URL is indexable.</param>
/// <param name="DeclaredCanonicalTarget">Declared rel=canonical target, when present.</param>
/// <param name="Terms">Primary terms (title and H1, orders 2 and 3).</param>
public sealed record UrlPrimaryTerms(
    long UrlId,
    string CanonicalUrl,
    bool IsIndexable,
    string? DeclaredCanonicalTarget,
    IReadOnlyList<PrimaryTerm> Terms);

/// <summary>
/// Site-wide n-gram analyzer (FS-CRAWL-002 §5): per-URL zone tokenization,
/// stop-word filtering, and top-K bounded 1/2/3-gram counting persisted to
/// <c>ngram_frequencies</c>; site aggregation materialized into
/// <c>ngram_site_summaries</c> on BOTH providers (§5.6: precomputed read-model
/// table, constant-cost explorer reads). Primary terms are density-weighted
/// (title 2.0, H1-only 1.5); H1 facts are cached from the per-URL pass or
/// recomputed from stored bodies via the tolerant parser (re-audit safe, no
/// network). Bounds discard and count — never fail (§5.6).
/// </summary>
public sealed class NGramAnalyzer : INgramAnalyzer
{
    private readonly IDbProvider _provider;
    private readonly IStopWordFilter _stopWords;
    private readonly IContentStore? _contentStore;
    private readonly ITolerantDomParser? _parser;
    private readonly NGramAnalyzerOptions _options;
    private readonly ConcurrentDictionary<(long AuditId, long UrlId), PageFacts> _factsCache = new();
    private readonly ConcurrentDictionary<long, IReadOnlyList<UrlPrimaryTerms>> _primaryCache = new();

    /// <summary>Creates the analyzer.</summary>
    /// <param name="provider">Database provider (both providers supported).</param>
    /// <param name="stopWords">Bundled + user stop-word filter.</param>
    /// <param name="contentStore">Stored-body access for H1/canonical recompute; null disables it.</param>
    /// <param name="parser">Tolerant parser for stored bodies; null disables recompute.</param>
    /// <param name="options">Configuration and bounds; null uses §5.5–§5.6 defaults.</param>
    public NGramAnalyzer(
        IDbProvider provider,
        IStopWordFilter stopWords,
        IContentStore? contentStore = null,
        ITolerantDomParser? parser = null,
        NGramAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(stopWords);
        _provider = provider;
        _stopWords = stopWords;
        _contentStore = contentStore;
        _parser = parser;
        _options = options ?? NGramAnalyzerOptions.Default;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NgramFrequencyDto>> AnalyzeUrlAsync(NgramUrlInput input, CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeUrlCoreAsync(input, cancellationToken).ConfigureAwait(false);
        return analysis.Rows;
    }

    /// <summary>
    /// Rich per-URL analysis: frequency rows plus discard-and-count counters.
    /// Rows are persisted to <c>ngram_frequencies</c> (idempotent per URL —
    /// prior rows for the audit/URL are replaced, supporting re-audit §9.6).
    /// Returned rows carry Id 0; the database assigns surrogates.
    /// </summary>
    /// <param name="input">URL zone texts and orders.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Frequency rows and bound counters.</returns>
    public async Task<NGramUrlAnalysis> AnalyzeUrlCoreAsync(NgramUrlInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var orders = input.Orders.Count > 0 ? input.Orders.Where(o => o >= 1).Distinct().ToArray() : [1, 2, 3];
        var rows = new List<NgramFrequencyDto>();
        long discarded = 0;

        foreach (var (zone, text) in input.ZoneTexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sentences = NGramTokenizer.TokenizeSentences(text, _options.MinTokenLength, _options.ExcludeNumericTokens);
            var filtered = sentences.Select(s => _stopWords.RemoveStopWords(s, input.Language)).ToArray();
            var ngrams = NGramTokenizer.BuildNgrams(filtered, orders);

            foreach (var (order, counts) in ngrams)
            {
                var total = counts.Values.Sum();
                if (total == 0)
                {
                    continue;
                }

                var ranked = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToArray();
                var kept = ranked.Take(_options.TopKPerUrlZoneOrder).ToArray();
                discarded += ranked.Length - kept.Length;
                foreach (var (term, count) in kept)
                {
                    rows.Add(new NgramFrequencyDto(0, input.AuditId, input.UrlId, zone, order, term, count, (double)count / total));
                }
            }
        }

        await PersistFrequenciesAsync(input.AuditId, input.UrlId, rows, cancellationToken).ConfigureAwait(false);
        _primaryCache.TryRemove(input.AuditId, out _);
        return new NGramUrlAnalysis(rows, new NGramBoundsCounters(TailTermsDiscarded: discarded));
    }

    /// <summary>
    /// Collects inbound-anchor zone text (§5.3): anchor text of recorded
    /// internal link edges targeting the URL. As recorded — scoping does not
    /// apply (anchors belong to source pages).
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urlId">Target URL identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Joined anchor texts (empty when none).</returns>
    public async Task<string> GetInboundAnchorTextAsync(long auditId, long urlId, CancellationToken cancellationToken = default)
    {
        using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `anchor_text` FROM `link_edges` WHERE `audit_id` = @auditId AND `target_url_id` = @urlId " +
            "AND `is_internal` = 1 AND `anchor_text` IS NOT NULL";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@urlId", urlId);
        var anchors = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var anchor = reader.GetString(0).Trim();
            if (anchor.Length > 0)
            {
                anchors.Add(anchor);
            }
        }

        return string.Join('\n', anchors);
    }

    /// <summary>
    /// Caches per-URL page facts from the live per-URL pass (H1 text with
    /// scoping applied, declared canonical target, language) so the site pass
    /// need not re-parse stored bodies.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urlId">URL identifier.</param>
    /// <param name="document">Parsed document.</param>
    /// <param name="scopedText">Resolved scoped text; null means unscoped.</param>
    public void CachePageFacts(long auditId, long urlId, DomDocument document, string? scopedText)
    {
        ArgumentNullException.ThrowIfNull(document);
        var facts = new PageFacts(
            ZoneTextExtractor.ExtractH1(document, scopedText),
            ZoneTextExtractor.ExtractCanonicalTarget(document, document.Url),
            ZoneTextExtractor.ExtractLanguage(document));
        _factsCache[(auditId, urlId)] = facts;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NgramSiteSummaryDto>> SummarizeSiteAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var (rows, _) = await SummarizeSiteCoreAsync(auditId, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// Rich site summarization: materialized rows plus discard-and-count
    /// counters for the DF/top-M retention bound (§5.6).
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Materialized summary rows and bound counters.</returns>
    public async Task<(IReadOnlyList<NgramSiteSummaryDto> Rows, NGramBoundsCounters Counters)> SummarizeSiteCoreAsync(
        long auditId, CancellationToken cancellationToken = default)
    {
        var (aggregates, urlCount, titleTf) = await LoadAggregatesAsync(auditId, cancellationToken).ConfigureAwait(false);
        var primaries = await ComputePrimaryTermsCoreAsync(auditId, aggregates, urlCount, titleTf, cancellationToken).ConfigureAwait(false);
        _primaryCache[auditId] = primaries;

        var primaryCounts = new Dictionary<(TextZone Zone, int Order, string Term), long>();
        foreach (var url in primaries)
        {
            foreach (var term in url.Terms)
            {
                var key = (term.Zone, term.Order, term.Term);
                primaryCounts[key] = primaryCounts.TryGetValue(key, out var existing) ? existing + 1 : 1;
            }
        }

        // Retention bound (§5.6): DF >= min plus the top M by total frequency.
        var retained = aggregates.Where(kv => kv.Value.Urls.Count >= _options.SiteMinDocumentFrequency).ToList();
        var retainedKeys = new HashSet<(TextZone, int, string)>(retained.Select(kv => kv.Key));
        var topM = aggregates.OrderByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key.Term, StringComparer.Ordinal)
            .Take(_options.TopMSiteTerms);
        foreach (var kv in topM)
        {
            if (retainedKeys.Add(kv.Key))
            {
                retained.Add(kv);
            }
        }

        var discarded = aggregates.Count - retained.Count;
        var rows = new List<NgramSiteSummaryDto>(retained.Count);
        foreach (var (key, aggregate) in retained.OrderBy(kv => kv.Key.Zone).ThenBy(kv => kv.Key.Order).ThenBy(kv => kv.Key.Term, StringComparer.Ordinal))
        {
            var df = aggregate.Urls.Count;
            rows.Add(new NgramSiteSummaryDto(
                0, auditId, key.Zone, key.Order, key.Term, df, aggregate.Total, aggregate.SumTf / df,
                primaryCounts.TryGetValue(key, out var primaryCount) ? primaryCount : 0));
        }

        await MaterializeSummariesAsync(auditId, rows, cancellationToken).ConfigureAwait(false);
        return (rows, new NGramBoundsCounters(SiteRowsDiscarded: discarded));
    }

    /// <summary>
    /// Computes density-weighted primary terms per URL (§5.4): title zone
    /// (weight 2.0) from persisted frequencies, H1-only text (weight 1.5) from
    /// cached or recomputed page facts. The H1 density uses the headings-zone
    /// site DF as its DF factor (the persisted zone containing H1). Results
    /// are cached per audit until new per-URL rows arrive.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Primary terms for every analyzed URL.</returns>
    public async Task<IReadOnlyList<UrlPrimaryTerms>> ComputePrimaryTermsAsync(long auditId, CancellationToken cancellationToken = default)
    {
        if (_primaryCache.TryGetValue(auditId, out var cached))
        {
            return cached;
        }

        var (aggregates, urlCount, titleTf) = await LoadAggregatesAsync(auditId, cancellationToken).ConfigureAwait(false);
        var primaries = await ComputePrimaryTermsCoreAsync(auditId, aggregates, urlCount, titleTf, cancellationToken).ConfigureAwait(false);
        _primaryCache[auditId] = primaries;
        return primaries;
    }

    private async Task<IReadOnlyList<UrlPrimaryTerms>> ComputePrimaryTermsCoreAsync(
        long auditId,
        Dictionary<(TextZone Zone, int Order, string Term), Aggregate> aggregates,
        int urlCount,
        Dictionary<(long UrlId, int Order), List<(string Term, double Tf)>> titleTf,
        CancellationToken cancellationToken)
    {
        var urlRecords = await LoadUrlRecordsAsync(auditId, cancellationToken).ConfigureAwait(false);
        var result = new List<UrlPrimaryTerms>(urlRecords.Count);
        foreach (var (urlId, canonicalUrl, isIndexable) in urlRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var terms = new List<PrimaryTerm>();
            foreach (var order in new[] { 2, 3 })
            {
                if (titleTf.TryGetValue((urlId, order), out var candidates))
                {
                    var best = SelectPrimary(candidates, TextZone.Title, order, aggregates, urlCount, _options.TitleZoneWeight);
                    if (best is not null)
                    {
                        terms.Add(best);
                    }
                }
            }

            var facts = await GetPageFactsAsync(auditId, urlId, cancellationToken).ConfigureAwait(false);
            if (facts.H1Text.Length > 0)
            {
                var sentences = NGramTokenizer.TokenizeSentences(facts.H1Text, _options.MinTokenLength, _options.ExcludeNumericTokens);
                var filtered = sentences.Select(s => _stopWords.RemoveStopWords(s, facts.Language ?? _options.PreferredLanguage)).ToArray();
                var h1Ngrams = NGramTokenizer.BuildNgrams(filtered, [2, 3]);
                foreach (var order in new[] { 2, 3 })
                {
                    if (!h1Ngrams.TryGetValue(order, out var counts) || counts.Count == 0)
                    {
                        continue;
                    }

                    var total = counts.Values.Sum();
                    var candidates = counts.Select(kv => (kv.Key, (double)kv.Value / total)).ToList();
                    var best = SelectPrimary(candidates, TextZone.Headings, order, aggregates, urlCount, _options.HeadingsZoneWeight);
                    if (best is not null)
                    {
                        terms.Add(best);
                    }
                }
            }

            result.Add(new UrlPrimaryTerms(urlId, canonicalUrl, isIndexable, facts.CanonicalTarget, terms));
        }

        return result;
    }

    private PrimaryTerm? SelectPrimary(
        List<(string Term, double Tf)> candidates,
        TextZone zone,
        int order,
        Dictionary<(TextZone Zone, int Order, string Term), Aggregate> aggregates,
        int urlCount,
        double weight)
    {
        PrimaryTerm? best = null;
        foreach (var (term, tf) in candidates)
        {
            if (tf < _options.MinTermFrequency)
            {
                continue;
            }

            var df = aggregates.TryGetValue((zone, order, term), out var aggregate) ? aggregate.Urls.Count : 1;
            var density = tf * Math.Log(1 + (double)Math.Max(urlCount, 1) / Math.Max(df, 1)) * weight;
            if (best is null || density > best.Density || (density == best.Density && string.CompareOrdinal(term, best.Term) < 0))
            {
                best = new PrimaryTerm(zone, order, term, tf, density);
            }
        }

        return best;
    }

    private async Task<PageFacts> GetPageFactsAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        if (_factsCache.TryGetValue((auditId, urlId), out var cached))
        {
            return cached;
        }

        if (_contentStore is null || _parser is null)
        {
            return PageFacts.Empty;
        }

        string? storageKey = null;
        var pageUrl = string.Empty;
        using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT a.`storage_key`, u.`canonical_url` FROM `content_artifacts` a " +
                "JOIN `url_records` u ON u.`id` = a.`url_id` " +
                "WHERE a.`audit_id` = @auditId AND a.`url_id` = @urlId AND a.`kind` = 0";
            AddParameter(command, "@auditId", auditId);
            AddParameter(command, "@urlId", urlId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                storageKey = reader.GetString(0);
                pageUrl = reader.GetString(1);
            }
        }

        if (storageKey is null)
        {
            return PageFacts.Empty;
        }

        using var stream = await _contentStore.OpenReadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return PageFacts.Empty;
        }

        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        var html = System.Text.Encoding.UTF8.GetString(memory.ToArray());
        var document = _parser.Parse(html, pageUrl);
        var facts = new PageFacts(
            ZoneTextExtractor.ExtractH1(document, null),
            ZoneTextExtractor.ExtractCanonicalTarget(document, pageUrl),
            ZoneTextExtractor.ExtractLanguage(document));
        _factsCache[(auditId, urlId)] = facts;
        return facts;
    }

    private async Task<(Dictionary<(TextZone Zone, int Order, string Term), Aggregate> Aggregates, int UrlCount,
        Dictionary<(long UrlId, int Order), List<(string Term, double Tf)>> TitleTf)> LoadAggregatesAsync(
        long auditId, CancellationToken cancellationToken)
    {
        var aggregates = new Dictionary<(TextZone, int, string), Aggregate>();
        var titleTf = new Dictionary<(long, int), List<(string, double)>>();
        var urls = new HashSet<long>();

        using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `url_id`, `zone`, `ngram_order`, `term`, `term_count`, `term_frequency` " +
            "FROM `ngram_frequencies` WHERE `audit_id` = @auditId";
        AddParameter(command, "@auditId", auditId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var urlId = reader.GetInt64(0);
            var zone = (TextZone)reader.GetInt32(1);
            var order = reader.GetInt32(2);
            var term = reader.GetString(3);
            var count = reader.GetInt64(4);
            var tf = reader.GetDouble(5);
            urls.Add(urlId);

            var key = (zone, order, term);
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new Aggregate();
                aggregates[key] = aggregate;
            }

            aggregate.Urls.Add(urlId);
            aggregate.Total += count;
            aggregate.SumTf += tf;

            if (zone == TextZone.Title && order is 2 or 3)
            {
                var titleKey = (urlId, order);
                if (!titleTf.TryGetValue(titleKey, out var list))
                {
                    list = [];
                    titleTf[titleKey] = list;
                }

                list.Add((term, tf));
            }
        }

        return (aggregates, urls.Count, titleTf);
    }

    private async Task<List<(long UrlId, string CanonicalUrl, bool IsIndexable)>> LoadUrlRecordsAsync(
        long auditId, CancellationToken cancellationToken)
    {
        var records = new List<(long, string, bool)>();
        using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `id`, `canonical_url`, `is_indexable` FROM `url_records` WHERE `audit_id` = @auditId ORDER BY `id`";
        AddParameter(command, "@auditId", auditId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0));
        }

        return records;
    }

    private async Task PersistFrequenciesAsync(long auditId, long urlId, IReadOnlyList<NgramFrequencyDto> rows, CancellationToken cancellationToken)
    {
        using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM `ngram_frequencies` WHERE `audit_id` = @auditId AND `url_id` = @urlId";
            AddParameter(delete, "@auditId", auditId);
            AddParameter(delete, "@urlId", urlId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO `ngram_frequencies` (`audit_id`, `url_id`, `zone`, `ngram_order`, `term`, `term_count`, `term_frequency`) " +
                "VALUES (@auditId, @urlId, @zone, @order, @term, @count, @tf)";
            AddParameter(insert, "@auditId", row.AuditId);
            AddParameter(insert, "@urlId", row.UrlId);
            AddParameter(insert, "@zone", (int)row.Zone);
            AddParameter(insert, "@order", row.Order);
            AddParameter(insert, "@term", row.Term);
            AddParameter(insert, "@count", row.Count);
            AddParameter(insert, "@tf", row.TermFrequency);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MaterializeSummariesAsync(long auditId, IReadOnlyList<NgramSiteSummaryDto> rows, CancellationToken cancellationToken)
    {
        using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM `ngram_site_summaries` WHERE `audit_id` = @auditId";
            AddParameter(delete, "@auditId", auditId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO `ngram_site_summaries` (`audit_id`, `zone`, `ngram_order`, `term`, `document_frequency`, `total_frequency`, `mean_term_frequency`, `primary_term_count`) " +
                "VALUES (@auditId, @zone, @order, @term, @df, @total, @meanTf, @primary)";
            AddParameter(insert, "@auditId", row.AuditId);
            AddParameter(insert, "@zone", (int)row.Zone);
            AddParameter(insert, "@order", row.Order);
            AddParameter(insert, "@term", row.Term);
            AddParameter(insert, "@df", row.DocumentFrequency);
            AddParameter(insert, "@total", row.TotalFrequency);
            AddParameter(insert, "@meanTf", row.MeanTermFrequency);
            AddParameter(insert, "@primary", row.PrimaryTermCount);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record PageFacts(string H1Text, string? CanonicalTarget, string? Language)
    {
        public static PageFacts Empty { get; } = new(string.Empty, null, null);
    }

    private sealed class Aggregate
    {
        public HashSet<long> Urls { get; } = [];

        public long Total { get; set; }

        public double SumTf { get; set; }
    }
}
