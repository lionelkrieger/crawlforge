using System.Globalization;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Persistence ports for the semantic linking pipeline (locked repositories;
/// composition-time injection by the Host).
/// </summary>
/// <param name="Urls">url_records reads.</param>
/// <param name="Artifacts">content_artifacts reads (content hashes).</param>
/// <param name="EmbeddingsRead">url_embeddings reads.</param>
/// <param name="EmbeddingsWrite">url_embeddings writes.</param>
/// <param name="LinkEdges">link_edges reads (existing-link exclusion).</param>
/// <param name="Ngrams">ngram_frequencies reads (anchor candidates).</param>
/// <param name="OpportunitiesRead">semantic_link_opportunities reads.</param>
/// <param name="OpportunitiesWrite">semantic_link_opportunities writes.</param>
/// <param name="ClustersRead">semantic_clusters reads.</param>
/// <param name="ClustersWrite">semantic_clusters writes.</param>
/// <param name="Findings">findings writes.</param>
/// <param name="Suppressions">finding_suppressions reads.</param>
/// <param name="MetricsRead">metric_summaries reads.</param>
/// <param name="MetricsWrite">metric_summaries writes.</param>
/// <param name="Audits">audits reads (snapshot fields).</param>
public sealed record SemanticPersistence(
    IReadRepository<UrlRecordDto> Urls,
    IReadRepository<ContentArtifactDto> Artifacts,
    IReadRepository<UrlEmbeddingDto> EmbeddingsRead,
    IWriteRepository<UrlEmbeddingDto> EmbeddingsWrite,
    IReadRepository<LinkEdgeDto> LinkEdges,
    IReadRepository<NgramFrequencyDto> Ngrams,
    IReadRepository<SemanticLinkOpportunityDto> OpportunitiesRead,
    IWriteRepository<SemanticLinkOpportunityDto> OpportunitiesWrite,
    IReadRepository<SemanticClusterDto> ClustersRead,
    IWriteRepository<SemanticClusterDto> ClustersWrite,
    IWriteRepository<FindingDto> Findings,
    IReadRepository<FindingSuppressionDto> Suppressions,
    IReadRepository<MetricSummaryDto> MetricsRead,
    IWriteRepository<MetricSummaryDto> MetricsWrite,
    IReadRepository<AuditDto> Audits);

/// <summary>
/// Semantic internal-linking pipeline (FS-CRAWL-002 §12): activates the
/// dormant url_embeddings table with strictly local vectors and runs the three
/// semantic passes — cannibalization clusters (≥ 0.88 default), bounded top-K
/// link opportunities in the similarity band (0.75–0.87 default) that share no
/// existing link in either direction, and low-relevance existing links
/// (&lt; 0.35 default). Inputs are STORED artifacts only (re-audit never
/// re-fetches); incremental re-embedding is keyed by content hash; provider
/// switches re-embed the audit without data migration. Opportunities are
/// persisted as SUGGESTIONS ONLY — the pipeline never mutates link_edges,
/// url_records, or content. Provider unavailability degrades to a local
/// diagnostic; audits never fail because of embeddings (§12.2).
/// </summary>
public sealed class SemanticLinkingPipeline : ISemanticLinkAnalyzer
{
    /// <summary>Diagnostic rule key when the embedding provider is unavailable.</summary>
    public const string EmbeddingsUnavailableRuleKey = "intelligence.embeddings-unavailable";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILocalEmbeddingProvider _provider;
    private readonly ITolerantDomParser _parser;
    private readonly IContentScopeResolver _scopeResolver;
    private readonly IContentStore _contentStore;
    private readonly SemanticPersistence _persistence;
    private readonly SemanticPipelineOptions _options;
    private readonly IProgress<SemanticJobProgress>? _progress;

    /// <summary>Creates the pipeline with composition-time injection.</summary>
    /// <param name="provider">Active local embedding provider (ONNX or loopback daemon).</param>
    /// <param name="parser">Tolerant DOM parser (accepted keystone surface).</param>
    /// <param name="scopeResolver">Content scope resolver (accepted keystone surface).</param>
    /// <param name="contentStore">Stored artifact content store.</param>
    /// <param name="persistence">Repository ports.</param>
    /// <param name="options">Local bounds and thresholds.</param>
    /// <param name="progress">Optional progress sink (Host relays to the loopback hub).</param>
    public SemanticLinkingPipeline(
        ILocalEmbeddingProvider provider,
        ITolerantDomParser parser,
        IContentScopeResolver scopeResolver,
        IContentStore contentStore,
        SemanticPersistence persistence,
        SemanticPipelineOptions? options = null,
        IProgress<SemanticJobProgress>? progress = null)
    {
        _provider = provider;
        _parser = parser;
        _scopeResolver = scopeResolver;
        _contentStore = contentStore;
        _persistence = persistence;
        _options = options ?? new SemanticPipelineOptions();
        _progress = progress;
    }

    /// <inheritdoc />
    public async Task<SemanticAnalysisResult> RunAsync(
        long auditId,
        SemanticAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        var bandLow = Math.Clamp(options.SimilarityThreshold, 0, 1);
        var topK = Math.Max(1, options.TopK);
        var audit = await _persistence.Audits.GetByIdAsync(auditId, cancellationToken);
        if (audit is null)
        {
            return new SemanticAnalysisResult([], []);
        }

        // §12.2: provider unavailable → local diagnostic, never a failed audit.
        EmbeddingProviderTestResult providerTest;
        try
        {
            providerTest = await _provider.TestConnectionAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            providerTest = new EmbeddingProviderTestResult(false, false, ex.Message);
        }

        if (!providerTest.IsReachable || !providerTest.ModelAvailable)
        {
            await WriteFindingAsync(audit, new FindingDraft(
                EmbeddingsUnavailableRuleKey,
                AuditCategory.Content,
                FindingSeverity.Notice,
                null,
                JsonSerializer.Serialize(new { reason = providerTest.Message ?? "provider unavailable", modelTag = _provider.ModelTag }, JsonOptions),
                "rec." + EmbeddingsUnavailableRuleKey), cancellationToken);
            Report(auditId, 0, 0, "unavailable", isFinal: true);
            return new SemanticAnalysisResult([], []);
        }

        var modelTag = _provider.ModelTag;
        var scope = ResolveAuditScope(audit);
        var eligible = await LoadEligibleUrlsAsync(auditId, cancellationToken);
        var existingLinks = await LoadExistingLinksAsync(auditId, eligible, cancellationToken);

        // Embedding phase — content-hash cache first, bounded concurrency,
        // per-URL failure isolation (§12.6).
        var cache = new EmbeddingCache();
        var chunker = new TextChunker(_options.MaxChunkChars, _options.OverlapChars);
        var vectors = new Dictionary<long, float[]>();
        var canonicalTargets = new Dictionary<long, string?>();
        var failures = 0;
        var diagnostics = new List<string>();
        var reused = 0;
        var embedded = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
        var pendingRows = new List<UrlEmbeddingDto>();
        var tagMismatch = await HasForeignModelTagAsync(auditId, modelTag, cancellationToken);
        if (tagMismatch)
        {
            // §12.3: mixing model tags within one audit is prohibited — a model
            // change requires a full re-embed (no data migration, same table).
            await DeleteAuditEmbeddingsAsync(auditId, cancellationToken);
        }

        foreach (var batch in eligible.Chunk(Math.Max(1, _options.MaxConcurrency) * 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = batch.Select(url => ProcessUrlAsync(
                auditId, url, modelTag, scope, chunker, cache, gate, cancellationToken)).ToArray();
            var outcomes = await Task.WhenAll(tasks);

            foreach (var outcome in outcomes)
            {
                if (outcome.Failure is not null)
                {
                    failures++;
                    if (diagnostics.Count < _options.MaxDiagnostics)
                    {
                        diagnostics.Add($"url {outcome.UrlId}: {outcome.Failure}");
                    }

                    continue;
                }

                if (outcome.Vector is null)
                {
                    continue; // no stored content artifact
                }

                vectors[outcome.UrlId] = outcome.Vector;
                canonicalTargets[outcome.UrlId] = outcome.CanonicalTarget;
                if (outcome.Reused)
                {
                    reused++;
                }

                pendingRows.Add(new UrlEmbeddingDto(
                    outcome.ExistingRowId ?? 0,
                    auditId,
                    outcome.UrlId,
                    new EmbeddingPayload(modelTag, outcome.Vector.Length, outcome.ContentHash, outcome.Vector).ToJson(),
                    modelTag));

                if (pendingRows.Count >= _options.BatchSize)
                {
                    await FlushEmbeddingsAsync(pendingRows, cancellationToken);
                }

                embedded++;
                if (stopwatch.Elapsed - lastReport >= TimeSpan.FromSeconds(1))
                {
                    lastReport = stopwatch.Elapsed;
                    Report(auditId, embedded, eligible.Count, "embedding");
                }
            }
        }

        await FlushEmbeddingsAsync(pendingRows, cancellationToken);
        Report(auditId, embedded, eligible.Count, "similarity");

        // Similarity passes over the bounded vector set.
        var orderedIds = vectors.Keys.OrderBy(id => id).ToArray();
        var similarity = new Dictionary<(long, long), double>();
        var unionFind = new UnionFind(orderedIds);
        var opportunityCandidates = new Dictionary<long, List<(long Target, double Score)>>();
        var lowRelevance = new List<(LinkEdgeDto Edge, double Score)>();

        for (var i = 0; i < orderedIds.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = orderedIds[i];
            for (var j = i + 1; j < orderedIds.Length; j++)
            {
                var targetId = orderedIds[j];
                var score = VectorMath.CosineSimilarity(vectors[sourceId], vectors[targetId]);
                similarity[(sourceId, targetId)] = score;
                similarity[(targetId, sourceId)] = score;

                if (score >= _options.CannibalizationThreshold)
                {
                    unionFind.Union(sourceId, targetId);
                }
                else if (score >= bandLow && !existingLinks.Contains((sourceId, targetId)))
                {
                    (opportunityCandidates.TryGetValue(sourceId, out var list) ? list : opportunityCandidates[sourceId] = []).Add((targetId, score));
                    (opportunityCandidates.TryGetValue(targetId, out var reverse) ? reverse : opportunityCandidates[targetId] = []).Add((sourceId, score));
                }
            }
        }

        foreach (var edge in existingLinks.Edges)
        {
            if (edge.TargetUrlId is { } targetId &&
                vectors.ContainsKey(edge.SourceUrlId) && vectors.ContainsKey(targetId) &&
                similarity.TryGetValue((edge.SourceUrlId, targetId), out var score) &&
                score < _options.RelevanceFloor)
            {
                lowRelevance.Add((edge, score));
            }
        }

        // Opportunities — bounded top-K per source, suggestions only.
        var opportunities = new List<SemanticLinkOpportunityDto>();
        foreach (var (sourceId, candidates) in opportunityCandidates.OrderBy(pair => pair.Key))
        {
            foreach (var (targetId, score) in VectorMath.TopK(candidates, c => c.Score, topK))
            {
                var anchors = await LoadAnchorCandidatesAsync(auditId, targetId, cancellationToken);
                opportunities.Add(new SemanticLinkOpportunityDto(
                    0, auditId, sourceId, targetId, score,
                    JsonSerializer.Serialize(anchors, JsonOptions)));
            }
        }

        await ReplaceOpportunitiesAsync(auditId, opportunities, cancellationToken);

        // Clusters — connected components at the cannibalization threshold.
        var clusters = new List<SemanticClusterDto>();
        var clusterFindingDrafts = new List<FindingDraft>();
        var clusterId = 0L;
        foreach (var component in unionFind.Components())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component.Count < 2)
            {
                continue;
            }

            clusterId++;
            var centroid = VectorMath.Normalize(VectorMath.MeanPool(component.Select(id => vectors[id]).ToArray()));
            var members = new List<object>();
            foreach (var memberId in component.OrderBy(id => id))
            {
                var centroidSimilarity = VectorMath.CosineSimilarity(vectors[memberId], centroid);
                clusters.Add(new SemanticClusterDto(0, auditId, clusterId, memberId, centroidSimilarity));
                members.Add(new { urlId = memberId, centroidSimilarity });
            }

            // Notice downgrade when every member declares one canonical target
            // (head fact captured during input extraction); else Warning.
            var declaredTargets = component.Select(id => canonicalTargets.GetValueOrDefault(id))
                .Where(target => !string.IsNullOrEmpty(target)).Distinct(StringComparer.Ordinal).ToArray();
            var allShareCanonical = declaredTargets.Length == 1 &&
                component.All(id => !string.IsNullOrEmpty(canonicalTargets.GetValueOrDefault(id)));
            var evidence = JsonSerializer.Serialize(new
            {
                clusterId,
                threshold = _options.CannibalizationThreshold,
                members,
                canonicalHints = component.Select(id => canonicalTargets.GetValueOrDefault(id)).ToArray(),
                sharedCanonicalTarget = allShareCanonical ? declaredTargets[0] : null,
            }, JsonOptions);
            clusterFindingDrafts.Add(new FindingDraft(
                FindingRuleKeys.ContentSemanticCannibalization,
                AuditCategory.Content,
                allShareCanonical ? FindingSeverity.Notice : FindingSeverity.Warning,
                component.OrderBy(id => id).First(),
                evidence,
                "rec." + FindingRuleKeys.ContentSemanticCannibalization));
        }

        await ReplaceClustersAsync(auditId, clusters, cancellationToken);

        // Findings — suppression-aware, consistent with keystone semantics.
        foreach (var draft in clusterFindingDrafts)
        {
            await WriteFindingAsync(audit, draft, cancellationToken);
        }

        foreach (var (edge, score) in VectorMath.TopK(lowRelevance, item => -item.Score, _options.MaxLowRelevanceFindings))
        {
            var evidence = JsonSerializer.Serialize(new
            {
                similarity = score,
                floor = _options.RelevanceFloor,
                anchorText = edge.AnchorText,
                domZone = edge.DomZone?.ToString(),
                targetUrlId = edge.TargetUrlId,
            }, JsonOptions);
            await WriteFindingAsync(audit, new FindingDraft(
                FindingRuleKeys.LinkingLowRelevanceLink,
                AuditCategory.Linking,
                FindingSeverity.Notice,
                edge.SourceUrlId,
                evidence,
                "rec." + FindingRuleKeys.LinkingLowRelevanceLink), cancellationToken);
        }

        if (failures > 0 || diagnostics.Count > 0)
        {
            await WriteFindingAsync(audit, new FindingDraft(
                "intelligence.embedding-failures",
                AuditCategory.Content,
                FindingSeverity.Notice,
                null,
                JsonSerializer.Serialize(new { failures, diagnostics }, JsonOptions),
                null), cancellationToken);
        }

        // Metrics (§12.7): semantic-opportunities + semantic-clusters.
        await ReplaceMetricAsync(auditId, MetricKeys.SemanticOpportunities, opportunities.Count, cancellationToken);
        await ReplaceMetricAsync(auditId, MetricKeys.SemanticClusters, (int)clusterId, cancellationToken);

        Report(auditId, embedded, eligible.Count, "completed", isFinal: true);
        return new SemanticAnalysisResult(opportunities, clusters);
    }

    private async Task<UrlOutcome> ProcessUrlAsync(
        long auditId,
        UrlRecordDto url,
        string modelTag,
        ContentScopeDefinition? scope,
        TextChunker chunker,
        EmbeddingCache cache,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var artifact = await LoadBodyArtifactAsync(auditId, url.Id, cancellationToken);
            if (artifact is null)
            {
                return new UrlOutcome(url.Id, null, null, null, null, false, null);
            }

            // Content-hash cache: identical content across URLs embeds once.
            if (cache.TryGet(artifact.ContentHash, modelTag, out var cached))
            {
                var existingCached = await EmbeddingCache.TryReuseStoredAsync(
                    _persistence.EmbeddingsRead, auditId, url.Id, artifact.ContentHash, modelTag, cancellationToken);
                return new UrlOutcome(url.Id, cached, artifact.ContentHash, null, existingCached?.Id, true, null);
            }

            // Stored-vector reuse: unchanged content + matching model tag.
            var stored = await EmbeddingCache.TryReuseStoredAsync(
                _persistence.EmbeddingsRead, auditId, url.Id, artifact.ContentHash, modelTag, cancellationToken);
            if (stored is not null)
            {
                var payload = EmbeddingPayload.TryParse(stored.EmbeddingPayloadJson)!;
                cache.Store(artifact.ContentHash, modelTag, payload.Values);
                return new UrlOutcome(url.Id, payload.Values, artifact.ContentHash, null, stored.Id, true, null);
            }

            await using var stream = await _contentStore.OpenReadAsync(artifact.StorageKey, cancellationToken);
            if (stream is null)
            {
                return new UrlOutcome(url.Id, null, null, null, null, false, "stored artifact missing from content store");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var html = await reader.ReadToEndAsync(cancellationToken);
            var document = _parser.Parse(html, url.CanonicalUrl);
            var (title, h1, canonicalTarget) = ExtractHeadFacts(document.Root);
            var scopedBody = _scopeResolver.Resolve(document, scope).Text;
            var input = TextChunker.AssembleInput(title, h1, scopedBody);
            var chunks = chunker.Chunk(input);
            if (chunks.Count == 0)
            {
                return new UrlOutcome(url.Id, null, null, canonicalTarget, null, false, "no embeddable text");
            }

            var chunkVectors = new List<float[]>(chunks.Count);
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vector = await _provider.EmbedAsync(chunk, cancellationToken);
                chunkVectors.Add(vector.Values.ToArray());
            }

            var pooled = VectorMath.Normalize(VectorMath.MeanPool(chunkVectors));
            cache.Store(artifact.ContentHash, modelTag, pooled);
            return new UrlOutcome(url.Id, pooled, artifact.ContentHash, canonicalTarget, null, false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UrlOutcome(url.Id, null, null, null, null, false, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<UrlRecordDto>> LoadEligibleUrlsAsync(long auditId, CancellationToken cancellationToken)
    {
        var eligible = new List<UrlRecordDto>();
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _persistence.Urls.ListAsync(new ListQuery(
                Offset: offset,
                Limit: 500,
                Filters: new Dictionary<string, string> { ["AuditId"] = Inv(auditId) }), cancellationToken);
            foreach (var url in page.Items)
            {
                if (url.IsIndexable && url.StatusClass == CrawlStatusClass.Success)
                {
                    eligible.Add(url);
                }
            }

            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.TotalCount || eligible.Count >= _options.MaxAnalyzedUrls)
            {
                break;
            }
        }

        if (eligible.Count > _options.MaxAnalyzedUrls)
        {
            eligible = eligible.Take(_options.MaxAnalyzedUrls).ToList();
        }

        return eligible;
    }

    private async Task<ExistingLinkSet> LoadExistingLinksAsync(
        long auditId,
        List<UrlRecordDto> eligible,
        CancellationToken cancellationToken)
    {
        var byCanonical = eligible.ToDictionary(url => url.CanonicalUrl, url => url.Id, StringComparer.Ordinal);
        var pairs = new HashSet<(long, long)>();
        var edges = new List<LinkEdgeDto>();
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _persistence.LinkEdges.ListAsync(new ListQuery(
                Offset: offset,
                Limit: 1000,
                Filters: new Dictionary<string, string> { ["AuditId"] = Inv(auditId) }), cancellationToken);
            foreach (var edge in page.Items)
            {
                edges.Add(edge);
                var targetId = edge.TargetUrlId ??
                    (byCanonical.TryGetValue(edge.CanonicalTargetUrl, out var resolved) ? resolved : (long?)null);
                if (targetId is { } target)
                {
                    // Either direction counts as an existing link (§12.5).
                    pairs.Add((edge.SourceUrlId, target));
                    pairs.Add((target, edge.SourceUrlId));
                }
            }

            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.TotalCount)
            {
                break;
            }
        }

        return new ExistingLinkSet(pairs, edges);
    }

    private async Task<ContentArtifactDto?> LoadBodyArtifactAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        var page = await _persistence.Artifacts.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 10,
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = Inv(auditId),
                ["UrlId"] = Inv(urlId),
            },
            SortBy: "Id",
            SortDescending: true), cancellationToken);
        return page.Items.FirstOrDefault(a => a.Kind == ContentArtifactKind.Body)
            ?? page.Items.FirstOrDefault(a => a.Kind == ContentArtifactKind.RenderedDom);
    }

    private async Task<IReadOnlyList<string>> LoadAnchorCandidatesAsync(long auditId, long targetUrlId, CancellationToken cancellationToken)
    {
        // Candidate anchor phrases from the target's top n-grams (§12.5).
        // Reads the locked ngram_frequencies table; absent rows (n-gram pass
        // not yet run) degrade gracefully to an empty candidate list.
        var page = await _persistence.Ngrams.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 200,
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = Inv(auditId),
                ["UrlId"] = Inv(targetUrlId),
            },
            SortBy: "Count",
            SortDescending: true), cancellationToken);

        return page.Items
            .Where(row => row.Order is 2 or 3 && row.Zone is TextZone.Title or TextZone.Headings or TextZone.Body)
            .Select(row => row.Term)
            .Distinct(StringComparer.Ordinal)
            .Take(_options.MaxAnchorCandidates)
            .ToArray();
    }

    private async Task<bool> HasForeignModelTagAsync(long auditId, string modelTag, CancellationToken cancellationToken)
    {
        var page = await _persistence.EmbeddingsRead.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 500,
            Filters: new Dictionary<string, string> { ["AuditId"] = Inv(auditId) }), cancellationToken);
        return page.Items.Any(row => !string.Equals(row.ModelTag, modelTag, StringComparison.Ordinal));
    }

    private async Task DeleteAuditEmbeddingsAsync(long auditId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await _persistence.EmbeddingsRead.ListAsync(new ListQuery(
                Offset: 0,
                Limit: 500,
                Filters: new Dictionary<string, string> { ["AuditId"] = Inv(auditId) }), cancellationToken);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var row in page.Items)
            {
                await _persistence.EmbeddingsWrite.DeleteAsync(row.Id, cancellationToken);
            }
        }
    }

    private async Task FlushEmbeddingsAsync(List<UrlEmbeddingDto> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Id > 0)
            {
                await _persistence.EmbeddingsWrite.UpdateAsync(row, cancellationToken);
            }
            else
            {
                await _persistence.EmbeddingsWrite.CreateAsync(row, cancellationToken);
            }
        }

        rows.Clear();
    }

    private async Task ReplaceOpportunitiesAsync(long auditId, List<SemanticLinkOpportunityDto> opportunities, CancellationToken cancellationToken)
    {
        await DeleteAllAsync(_persistence.OpportunitiesRead, _persistence.OpportunitiesWrite, auditId, row => row.Id, cancellationToken);
        foreach (var opportunity in opportunities)
        {
            await _persistence.OpportunitiesWrite.CreateAsync(opportunity, cancellationToken);
        }
    }

    private async Task ReplaceClustersAsync(long auditId, List<SemanticClusterDto> clusters, CancellationToken cancellationToken)
    {
        await DeleteAllAsync(_persistence.ClustersRead, _persistence.ClustersWrite, auditId, row => row.Id, cancellationToken);
        foreach (var cluster in clusters)
        {
            await _persistence.ClustersWrite.CreateAsync(cluster, cancellationToken);
        }
    }

    private async Task ReplaceMetricAsync(long auditId, string metricKey, double value, CancellationToken cancellationToken)
    {
        var page = await _persistence.MetricsRead.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 100,
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = Inv(auditId),
                ["MetricKey"] = metricKey,
            }), cancellationToken);
        foreach (var row in page.Items)
        {
            await _persistence.MetricsWrite.DeleteAsync(row.Id, cancellationToken);
        }

        await _persistence.MetricsWrite.CreateAsync(new MetricSummaryDto(
            0, auditId, metricKey, "{}", value), cancellationToken);
    }

    private async Task WriteFindingAsync(AuditDto audit, FindingDraft draft, CancellationToken cancellationToken)
    {
        var suppressions = await _persistence.Suppressions.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 10000,
            Filters: new Dictionary<string, string> { ["ProjectId"] = Inv(audit.ProjectId) }), cancellationToken);
        var isSuppressed = IsSuppressed(draft, suppressions.Items);
        await _persistence.Findings.CreateAsync(new FindingDto(
            0,
            audit.Id,
            draft.RuleKey,
            draft.Category,
            draft.Severity,
            draft.UrlId,
            draft.EvidenceJson,
            draft.RecommendationKey,
            isSuppressed), cancellationToken);
    }

    /// <summary>
    /// Suppression matching consistent with the keystone FindingEmitter: every
    /// declared dimension must match; dimensionless rows suppress nothing;
    /// FindingScope matches against the rule key.
    /// </summary>
    /// <param name="draft">Finding draft.</param>
    /// <param name="suppressions">Project suppression rows.</param>
    /// <returns>True when the finding is suppressed.</returns>
    public static bool IsSuppressed(FindingDraft draft, IReadOnlyList<FindingSuppressionDto> suppressions)
    {
        foreach (var row in suppressions)
        {
            if (!row.IsSuppressed)
            {
                continue;
            }

            var hasDimension = row.RuleKey is not null || row.UrlId is not null || row.FindingScope is not null;
            if (!hasDimension)
            {
                continue;
            }

            if (row.RuleKey is not null && !string.Equals(row.RuleKey, draft.RuleKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (row.UrlId is not null && row.UrlId != draft.UrlId)
            {
                continue;
            }

            if (row.FindingScope is not null && !string.Equals(row.FindingScope, draft.RuleKey, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private ContentScopeDefinition? ResolveAuditScope(AuditDto audit)
    {
        if (string.IsNullOrWhiteSpace(audit.ResolvedContentScopeJson))
        {
            return null; // baseline-preserving default
        }

        try
        {
            return JsonSerializer.Deserialize<ContentScopeDefinition>(audit.ResolvedContentScopeJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null; // malformed snapshot degrades to baseline, never fatal
        }
    }

    private static (string? Title, string? H1, string? CanonicalTarget) ExtractHeadFacts(DomNode root)
    {
        string? title = null, h1 = null, canonical = null;
        var stack = new Stack<DomNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.TagName == "title" && title is null)
            {
                title = NodeText(node);
            }
            else if (node.TagName == "h1" && h1 is null)
            {
                h1 = NodeText(node);
            }
            else if (node.TagName == "link" && canonical is null &&
                node.Attributes.TryGetValue("rel", out var rel) &&
                rel.Contains("canonical", StringComparison.OrdinalIgnoreCase) &&
                node.Attributes.TryGetValue("href", out var href))
            {
                canonical = href;
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        return (title, h1, canonical);
    }

    private static string NodeText(DomNode node)
    {
        var builder = new StringBuilder();
        AppendText(node, builder);
        return builder.ToString().Trim();
    }

    private static void AppendText(DomNode node, StringBuilder builder)
    {
        if (node.TagName is "script" or "style")
        {
            return;
        }

        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            AppendText(child, builder);
        }
    }

    private void Report(long auditId, int embedded, int total, string phase, bool isFinal = false) =>
        _progress?.Report(new SemanticJobProgress(auditId, embedded, total, _provider.ModelTag, phase, isFinal));

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task DeleteAllAsync<T>(
        IReadRepository<T> read,
        IWriteRepository<T> write,
        long auditId,
        Func<T, long> idSelector,
        CancellationToken cancellationToken)
        where T : class
    {
        while (true)
        {
            var page = await read.ListAsync(new ListQuery(
                Offset: 0,
                Limit: 500,
                Filters: new Dictionary<string, string> { ["AuditId"] = Inv(auditId) }), cancellationToken);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var row in page.Items)
            {
                await write.DeleteAsync(idSelector(row), cancellationToken);
            }
        }
    }

    private sealed record UrlOutcome(
        long UrlId,
        float[]? Vector,
        string? ContentHash,
        string? CanonicalTarget,
        long? ExistingRowId,
        bool Reused,
        string? Failure);

    private sealed record ExistingLinkSet(HashSet<(long Source, long Target)> Pairs, List<LinkEdgeDto> Edges)
    {
        public bool Contains((long Source, long Target) pair) => Pairs.Contains(pair);
    }

    private sealed class UnionFind
    {
        private readonly Dictionary<long, long> _parent;

        public UnionFind(IEnumerable<long> ids)
        {
            _parent = ids.ToDictionary(id => id, id => id);
        }

        public long Find(long id)
        {
            var root = id;
            while (_parent[root] != root)
            {
                root = _parent[root];
            }

            while (_parent[id] != root)
            {
                var next = _parent[id];
                _parent[id] = root;
                id = next;
            }

            return root;
        }

        public void Union(long a, long b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
            {
                _parent[Math.Max(rootA, rootB)] = Math.Min(rootA, rootB);
            }
        }

        public IEnumerable<List<long>> Components() =>
            _parent.Keys.GroupBy(Find).Select(group => group.ToList());
    }
}
