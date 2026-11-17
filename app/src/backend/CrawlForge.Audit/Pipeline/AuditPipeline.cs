using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Pipeline;

/// <summary>
/// Eight-step audit pipeline orchestrator (FS-CRAWL-001 §7.2), consuming STORED
/// ARTIFACTS ONLY: a re-audit never re-fetches — the stored response body (or
/// pre-parsed document) and rendered DOM arrive through the page context.
/// Steps: (1) reuse stored HTML, (2) tolerant parse, (3) scope resolution +
/// element inventory, (4) per-URL rule passes through the injected analyzer
/// family with per-rule failure isolation, (5) cross-URL aggregation is an
/// audit-level pass over indexed views (driven by the audit runner; this
/// orchestrator supplies the per-URL facts it indexes), (6) finding emission
/// with scoped-basis evidence for text findings, (7) metric aggregation,
/// (8) report readiness — the returned result is the page-level readiness
/// signal. Scoping propagates to text passes only: head facts and link
/// discovery are extracted from the full document (FS-CRAWL-002 §4.3).
/// </summary>
public sealed class AuditPipeline : IAuditPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ITolerantDomParser _parser;
    private readonly IElementInventoryExtractor _extractor;
    private readonly IContentScopeResolver _scopeResolver;
    private readonly IAuditRuleAnalyzer _ruleHost;
    private readonly IReadRepository<ProjectDto>? _projects;
    private readonly ContentScopeProfileStore? _profileStore;
    private readonly IIndexabilityAnalyzer? _indexability;
    private readonly IHeaderHtmlEquivalencyAnalyzer? _headerHtmlEquivalency;
    private readonly IResponseRenderedComparer? _responseRenderedComparer;
    private readonly ISoft404Detector? _soft404Detector;

    /// <summary>Creates the pipeline with composition-time analyzer injection.</summary>
    /// <param name="parser">Tolerant DOM parser.</param>
    /// <param name="extractor">Element inventory extractor.</param>
    /// <param name="scopeResolver">Content scope resolver.</param>
    /// <param name="ruleHost">Generic rule host composing the analyzer family.</param>
    /// <param name="projects">Optional project read repository (project-level scope default).</param>
    /// <param name="profileStore">Optional scope profile store (profile references).</param>
    /// <param name="indexability">Optional indexability analyzer.</param>
    /// <param name="headerHtmlEquivalency">Optional header/HTML equivalency analyzer.</param>
    /// <param name="responseRenderedComparer">Optional response-vs-rendered comparer.</param>
    /// <param name="soft404Detector">Optional soft-404 detector.</param>
    public AuditPipeline(
        ITolerantDomParser parser,
        IElementInventoryExtractor extractor,
        IContentScopeResolver scopeResolver,
        IAuditRuleAnalyzer ruleHost,
        IReadRepository<ProjectDto>? projects = null,
        ContentScopeProfileStore? profileStore = null,
        IIndexabilityAnalyzer? indexability = null,
        IHeaderHtmlEquivalencyAnalyzer? headerHtmlEquivalency = null,
        IResponseRenderedComparer? responseRenderedComparer = null,
        ISoft404Detector? soft404Detector = null)
    {
        _parser = parser;
        _extractor = extractor;
        _scopeResolver = scopeResolver;
        _ruleHost = ruleHost;
        _projects = projects;
        _profileStore = profileStore;
        _indexability = indexability;
        _headerHtmlEquivalency = headerHtmlEquivalency;
        _responseRenderedComparer = responseRenderedComparer;
        _soft404Detector = soft404Detector;
    }

    /// <inheritdoc />
    public async Task<PageAuditResult> RunAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        // Step 1 — fetch/reuse: stored artifacts only; never re-fetch on audit.
        var document = context.Document;
        if (document is null && context.Fetch is { } storedFetch)
        {
            document = _parser.Parse(Encoding.UTF8.GetString(storedFetch.Body.Span), context.Url.CanonicalUrl);
        }

        if (document is null)
        {
            return new PageAuditResult([PipelineDiagnostics.NoDocument(context.Url.Id)], [], []);
        }

        var findings = new List<FindingDraft>();

        // Step 2 — tolerant parse diagnostics.
        if (document.ParseWarnings.Count > 0)
        {
            findings.Add(PipelineDiagnostics.ParseWarnings(document.ParseWarnings, context.Url.Id));
        }

        // Scope resolution runs after parsing and before text-sensitive passes
        // (FS-CRAWL-002 §4.3); in rendering mode it applies to the post-render tree.
        var scope = await ResolveScopeAsync(context.Configuration, cancellationToken);
        var effectiveDocument = context.RenderedDocument ?? document;
        var resolution = ResolveScope(effectiveDocument, scope);

        // Step 3 — element inventory from the FULL document: head facts and
        // link discovery are scoping non-consumers (§4.3).
        var inventory = new List<ElementInventoryItem>(await _extractor.ExtractAsync(document, cancellationToken))
        {
            ContentScopeItem(resolution, scope),
        };
        if (context.Fetch is { } fetch)
        {
            inventory.Add(HeaderDirectivesItem(fetch.Headers));
        }

        // Steps 4/5 — per-URL rule passes; each analyzer isolated (§7.6).
        var enriched = context with { Document = document, ScopedText = resolution.VisibleText };
        findings.AddRange(await RunIsolatedAsync(_ruleHost, enriched, cancellationToken));

        if (_indexability is not null)
        {
            try
            {
                findings.AddRange(await _indexability.AnalyzeAsync(enriched, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                findings.Add(PipelineDiagnostics.AnalyzerFailure(nameof(IIndexabilityAnalyzer), ex.Message, context.Url.Id));
            }
        }

        if (_headerHtmlEquivalency is not null)
        {
            try
            {
                findings.AddRange(await _headerHtmlEquivalency.AnalyzeAsync(enriched, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                findings.Add(PipelineDiagnostics.AnalyzerFailure(nameof(IHeaderHtmlEquivalencyAnalyzer), ex.Message, context.Url.Id));
            }
        }

        if (_responseRenderedComparer is not null)
        {
            try
            {
                var comparison = await _responseRenderedComparer.CompareAsync(enriched, cancellationToken);
                findings.AddRange(comparison.Findings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                findings.Add(PipelineDiagnostics.AnalyzerFailure(nameof(IResponseRenderedComparer), ex.Message, context.Url.Id));
            }
        }

        if (_soft404Detector is not null)
        {
            try
            {
                var verdict = await _soft404Detector.DetectAsync(enriched, cancellationToken);
                if (verdict.IsSoft404)
                {
                    findings.Add(new FindingDraft(
                        PipelineDiagnostics.Soft404RuleKey,
                        AuditCategory.Indexability,
                        FindingSeverity.Warning,
                        context.Url.Id,
                        PipelineDiagnostics.MergeEvidence(verdict.EvidenceJson, new Dictionary<string, object?>
                        {
                            ["confidence"] = verdict.Confidence,
                        }),
                        RecommendationKeys.ForRule(PipelineDiagnostics.Soft404RuleKey)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                findings.Add(PipelineDiagnostics.AnalyzerFailure(nameof(ISoft404Detector), ex.Message, context.Url.Id));
            }
        }

        // Step 6 — finding emission: text-based findings note the scoped basis
        // in evidence so judgments are reproducible (§4.5).
        if (scope is not null)
        {
            for (var i = 0; i < findings.Count; i++)
            {
                if (findings[i].Category == AuditCategory.Content)
                {
                    findings[i] = findings[i] with
                    {
                        EvidenceJson = PipelineDiagnostics.MergeEvidence(findings[i].EvidenceJson, new Dictionary<string, object?>
                        {
                            ["scopedBasis"] = true,
                            ["scopedWordCount"] = resolution.ScopedWordCount,
                        }),
                    };
                }
            }
        }

        // Step 7 — metric aggregation (§8.1 aggregate pattern).
        var metrics = new List<MetricContribution>
        {
            new(
                MetricKeys.ScopedWordCount,
                JsonSerializer.Serialize(new
                {
                    urlId = context.Url.Id,
                    host = context.Url.Host,
                    fullWordCount = resolution.FullWordCount,
                    removedWordCount = resolution.RemovedWordCount,
                }, JsonOptions),
                resolution.ScopedWordCount),
        };

        // Step 8 — report readiness: the page result is the readiness signal;
        // the audit runner marks the audit reportable and notifies the hub.
        return new PageAuditResult(findings, inventory, metrics);
    }

    private async Task<ContentScopeDefinition?> ResolveScopeAsync(
        CrawlConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        // §4.2 priority: audit-level inline scope overrides; absent audit scope
        // inherits the project default; nothing configured → baseline.
        if (configuration.ContentScope is { } inline)
        {
            return inline;
        }

        if (configuration.ContentScopeProfileId is { } profileId && _profileStore is not null)
        {
            var profileScope = await _profileStore.ResolveDefinitionAsync(profileId, cancellationToken);
            if (profileScope is not null)
            {
                return profileScope;
            }
        }

        if (_projects is not null)
        {
            var project = await _projects.GetByIdAsync(configuration.ProjectId, cancellationToken);
            if (project?.ContentScope is { } projectScope)
            {
                return projectScope;
            }
        }

        return null;
    }

    private ScopeResolution ResolveScope(DomDocument document, ContentScopeDefinition? scope)
    {
        if (_scopeResolver is ContentScopeResolver detailed)
        {
            return detailed.ResolveDetailed(document, scope);
        }

        var scoped = _scopeResolver.Resolve(document, scope);
        var full = _scopeResolver.Resolve(document, null);
        return new ScopeResolution(
            scoped,
            IncludeMatchCount: 0,
            ExcludeMatchCount: 0,
            FullWordCount: full.WordCount,
            ScopedWordCount: scoped.WordCount,
            RemovedWordCount: Math.Max(0, full.WordCount - scoped.WordCount),
            PerSelectorCounts: []);
    }

    private static ElementInventoryItem ContentScopeItem(ScopeResolution resolution, ContentScopeDefinition? scope) =>
        new(
            ElementGroups.ContentScope,
            JsonSerializer.Serialize(new
            {
                configured = scope is not null,
                includeMatchCount = resolution.IncludeMatchCount,
                excludeMatchCount = resolution.ExcludeMatchCount,
                fullWordCount = resolution.FullWordCount,
                scopedWordCount = resolution.ScopedWordCount,
                removedWordCount = resolution.RemovedWordCount,
                includeSelectors = scope?.IncludeSelectors ?? [],
                excludeSelectors = scope?.ExcludeSelectors ?? [],
                perSelector = resolution.PerSelectorCounts,
            }, JsonOptions),
            Ordinal: 0);

    private static ElementInventoryItem HeaderDirectivesItem(IReadOnlyDictionary<string, string> headers)
    {
        string? Header(string name) =>
            headers.TryGetValue(name, out var value) ? value : null;

        return new ElementInventoryItem(
            ElementGroups.HeaderDirectives,
            JsonSerializer.Serialize(new
            {
                xRobotsTag = Header("x-robots-tag"),
                link = Header("link"),
                contentLanguage = Header("content-language"),
                contentType = Header("content-type"),
                xFrameOptions = Header("x-frame-options"),
                strictTransportSecurity = Header("strict-transport-security"),
                contentSecurityPolicy = Header("content-security-policy"),
                headerCount = headers.Count,
            }, JsonOptions),
            Ordinal: 0);
    }

    private static async Task<IReadOnlyList<FindingDraft>> RunIsolatedAsync(
        IAuditRuleAnalyzer analyzer,
        PageAuditContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await analyzer.AnalyzeAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [PipelineDiagnostics.AnalyzerFailure(analyzer.GetType().Name, ex.Message, context.Url.Id)];
        }
    }
}
