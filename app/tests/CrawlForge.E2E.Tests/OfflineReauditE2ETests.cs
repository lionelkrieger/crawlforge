using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Custom;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.E2E.Tests.Fixtures;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// Offline re-audit proofs (WP-07-01, criteria #15/#24/#30/#31/#35/#37):
/// re-audit reuses stored bodies with ZERO network access (request counts
/// proven via LoopbackOrigin), content comparison renders from local
/// aggregates, custom extraction/search/JS re-run from stored artifacts, WAF
/// detections are copied and block metrics re-derived, and scoped metrics are
/// re-derived offline. The lifecycle contract <c>ReAuditAsync(refresh:false)</c>
/// is the offline mechanism, proven through both the DI surface and the REST
/// reaudit route (criterion #15).
/// </summary>
public sealed class OfflineReauditE2ETests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Index + two content pages with boilerplate chrome.</summary>
    private static Task<LoopbackOrigin> ContentSiteAsync() =>
        LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page("Index",
                FixturePages.Links("/p/1", "/p/2"))));
            routes.Map("/p/1", OriginResponse.Html(
                "<!doctype html><html lang=\"en\"><head><title>Page One</title></head><body>" +
                "<nav>Navigation boilerplate words that repeat across every page of the site</nav>" +
                "<main><h1>Page One</h1><p>Unique content of page one with plenty of words to analyze and score.</p>" +
                "<p><span class=\"sku\">SKU-42</span> widget widget widget</p></main>" +
                "<footer>Footer boilerplate words that repeat across every page of the site</footer>" +
                "</body></html>"));
            routes.Map("/p/2", OriginResponse.Html(FixturePages.Page("Page Two",
                "<p>Unique content of page two with plenty of words to analyze and score.</p>")));
        });

    private static async Task<(long AuditId, int BaselineRequests)> CrawlAsync(
        E2EBackend backend, LoopbackOrigin origin,
        Func<CrawlConfigurationDto, CrawlConfigurationDto>? customize = null)
    {
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl, customize: customize);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        return (auditId, origin.RequestCount);
    }

    /// <summary>
    /// Rebuilds a page context from STORED artifacts exactly as the engine's
    /// analysis-only pass does — no fetcher is involved.
    /// </summary>
    private static async Task<PageAuditContext> StoredContextAsync(
        E2EBackend backend, long auditId, UrlRecordDto url, RenderMode renderMode)
    {
        var artifacts = await backend.Service<IReadRepository<ContentArtifactDto>>().ListAsync(
            new ListQuery(0, 10, new Dictionary<string, string>
            {
                ["auditId"] = auditId.ToString(),
                ["urlId"] = url.Id.ToString()
            }));
        var body = artifacts.Items.First(a => a.Kind == ContentArtifactKind.Body);
        await using var stream = await backend.Service<IContentStore>().OpenReadAsync(body.StorageKey);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        await stream!.CopyToAsync(memory);
        var bytes = memory.ToArray();

        var audit = await backend.Service<IReadRepository<AuditDto>>().GetByIdAsync(auditId);
        var configuration = await backend.Service<IReadRepository<CrawlConfigurationDto>>()
            .GetByIdAsync(audit!.ConfigurationId);
        configuration = configuration! with { RenderingMode = renderMode };

        var document = backend.Service<ITolerantDomParser>()
            .Parse(Encoding.UTF8.GetString(bytes), url.CanonicalUrl);
        var fetch = new HttpFetchResult(
            url.CanonicalUrl, url.FinalStatusCode ?? 200,
            new Dictionary<string, string>(), bytes, TimeSpan.Zero, TimeSpan.Zero);
        var scopedText = backend.Service<IContentScopeResolver>()
            .Resolve(document, configuration.ContentScope);
        return new PageAuditContext(auditId, url, configuration, fetch, document, null, scopedText, null);
    }

    [Fact]
    public async Task ReAudit_Offline_ReproducesDatasetFindingsAndScores_WithZeroNetwork()
    {
        await using var origin = await ContentSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (auditId, baselineRequests) = await CrawlAsync(backend, origin);

        var engine = backend.Service<IAuditLifecyclePort>();
        var reAuditId = await engine.ReAuditAsync(auditId, refresh: false);
        var reAudit = await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // Criterion #15: zero network — the origin saw no new requests.
        Assert.Equal(baselineRequests, origin.RequestCount);

        // Same dataset shape under the new audit.
        var sourceUrls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        var reUrls = await backend.ListByAuditAsync<UrlRecordDto>(reAuditId);
        Assert.Equal(sourceUrls.Count, reUrls.Count);
        Assert.Equal(
            sourceUrls.Select(u => u.CanonicalUrl).OrderBy(u => u, StringComparer.Ordinal),
            reUrls.Select(u => u.CanonicalUrl).OrderBy(u => u, StringComparer.Ordinal));
        Assert.All(reUrls, u => Assert.Equal(CrawlStatusClass.Success, u.StatusClass));

        // Findings re-emitted from stored bodies (same rule-key surface).
        var sourceFindings = await backend.ListByAuditAsync<FindingDto>(auditId);
        var reFindings = await backend.ListByAuditAsync<FindingDto>(reAuditId);
        Assert.NotEmpty(sourceFindings);
        Assert.Equal(
            sourceFindings.Select(f => f.RuleKey).Distinct().OrderBy(k => k, StringComparer.Ordinal),
            reFindings.Select(f => f.RuleKey).Distinct().OrderBy(k => k, StringComparer.Ordinal));

        // Scores re-computed → report readiness.
        var scores = await backend.ListByAuditAsync<AuditScoreDto>(reAuditId);
        Assert.NotEmpty(scores);

        // Analysis counters match the source run.
        using var sourceCounters = JsonDocument.Parse((await backend.Service<IReadRepository<AuditDto>>().GetByIdAsync(auditId))!.CountersJson);
        using var reCounters = JsonDocument.Parse(reAudit.CountersJson);
        Assert.Equal(
            sourceCounters.RootElement.GetProperty("analyzed").GetInt64(),
            reCounters.RootElement.GetProperty("analyzed").GetInt64());
    }

    [Fact]
    public async Task ReAudit_Offline_ContentComparison_RendersFromStoredAggregates()
    {
        await using var origin = await ContentSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (auditId, baselineRequests) = await CrawlAsync(backend, origin);
        var reAuditId = await backend.Service<IAuditLifecyclePort>().ReAuditAsync(auditId, refresh: false);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        var pageOne = urls.First(u => u.Path == "/p/1");

        // Content comparison across the two audits: identical stored bodies →
        // no diffs, fully offline.
        var compare = await backend.Http.GetAsync(
            $"/api/audits/compare/content?auditIdA={auditId}&auditIdB={reAuditId}&canonicalUrl={Uri.EscapeDataString(pageOne.CanonicalUrl)}");
        Assert.True(compare.IsSuccessStatusCode);
        var envelope = await WireJson.EnvelopeAsync(compare);
        var data = envelope.GetProperty("data");
        Assert.False(data.GetProperty("title").GetProperty("changed").GetBoolean());
        Assert.False(data.GetProperty("text").GetProperty("changed").GetBoolean());
        Assert.Equal(0, data.GetProperty("metadataChanges").GetArrayLength());

        // Finding comparison: identical datasets → nothing new or resolved.
        var findingCompare = await backend.Http.GetAsync($"/api/audits/compare?auditIdA={auditId}&auditIdB={reAuditId}");
        Assert.True(findingCompare.IsSuccessStatusCode);
        var findingEnvelope = await WireJson.EnvelopeAsync(findingCompare);
        Assert.Equal(0, findingEnvelope.GetProperty("data").GetProperty("newFindings").GetArrayLength());
        Assert.Equal(0, findingEnvelope.GetProperty("data").GetProperty("resolvedFindings").GetArrayLength());

        // Both comparison surfaces stayed offline.
        Assert.Equal(baselineRequests, origin.RequestCount);
    }

    [Fact]
    public async Task ReAudit_Offline_CustomExtractionSearchAndJs_RerunFromStoredBodies()
    {
        await using var origin = await ContentSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (auditId, baselineRequests) = await CrawlAsync(backend, origin);
        var reAuditId = await backend.Service<IAuditLifecyclePort>().ReAuditAsync(auditId, refresh: false);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var reUrls = await backend.ListByAuditAsync<UrlRecordDto>(reAuditId);
        var pageOne = reUrls.First(u => u.Path == "/p/1");
        // Rendering-mode flag enables the snippet runner over the STORED DOM;
        // no headless fetch occurs (Jint evaluates stored artifacts only).
        var context = await StoredContextAsync(backend, reAuditId, pageOne, RenderMode.Auto);

        // Custom extraction re-runs offline: the SKU rule captures its value.
        var extractor = new CustomContentExtractor();
        var values = await extractor.ExtractAsync(context,
            [new CustomExtractionRule("sku", ".sku", ExtractedValueType.Text)]);
        var sku = Assert.Single(values);
        Assert.False(sku.IsInvalid);
        Assert.Contains("SKU-42", sku.ValueJson!);

        // Custom search re-runs offline against the scoped visible text.
        var search = new CustomContentSearch();
        var matches = await search.SearchAsync(context, [new CustomSearchItem("widget", "widget")]);
        var widget = Assert.Single(matches);
        Assert.True(widget.MatchCount >= 3, $"Expected >=3 widget matches, got {widget.MatchCount}.");

        // JS snippet re-runs offline in the sandboxed interpreter.
        var snippets = new JintSnippetRunner();
        var outputs = await snippets.RunAsync(context,
            [new JsSnippetDefinition("words", "Word count",
                "cf.submit('words', cf.text.split(/\\s+/).filter(function (w) { return w.length > 0; }).length);")]);
        var output = Assert.Single(outputs);
        Assert.Contains("words", output.ColumnsJson);
        Assert.NotEqual("[]", output.RowsJson);

        // The entire re-run touched no network.
        Assert.Equal(baselineRequests, origin.RequestCount);
    }

    [Fact]
    public async Task ReAudit_Offline_WafDetectionsCopiedAndBlockMetricsRederived()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page("Index", FixturePages.Links("/challenge", "/ok"))));
            routes.Map("/challenge", OriginResponse.Html(FixturePages.Page(
                "Challenge", "<div>challenge-platform</div><p>Please wait while we verify your browser.</p>"))
                .WithHeader("cf-mitigated", "challenge"));
            routes.Map("/ok", OriginResponse.Html(FixturePages.Page("OK", "<p>Perfectly normal content page.</p>")));
        });
        await using var backend = await E2EBackend.StartAsync();
        var (auditId, baselineRequests) = await CrawlAsync(backend, origin);

        var sourceDetections = await backend.ListByAuditAsync<WafDetectionDto>(auditId);
        Assert.NotEmpty(sourceDetections);

        var reAuditId = await backend.Service<IAuditLifecyclePort>().ReAuditAsync(auditId, refresh: false);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // Zero network: blocked pages are never re-fetched, ever.
        Assert.Equal(baselineRequests, origin.RequestCount);
        Assert.Equal(1, origin.CountFor("/challenge"));

        // Detections copied with remapped URL references; blocked verdict kept.
        var reDetections = await backend.ListByAuditAsync<WafDetectionDto>(reAuditId);
        Assert.Equal(sourceDetections.Count, reDetections.Count);
        Assert.Equal(sourceDetections[0].Provider, reDetections[0].Provider);
        var reUrls = await backend.ListByAuditAsync<UrlRecordDto>(reAuditId);
        Assert.Contains(reUrls, u => u.StatusClass == CrawlStatusClass.Blocked && u.BlockReason == "waf-challenge");

        // Site-wide block metrics re-derived offline from stored records.
        var reMetrics = await backend.ListByAuditAsync<MetricSummaryDto>(reAuditId);
        Assert.Contains(reMetrics, m => m.MetricKey == MetricKeys.WafBlocks && m.Value >= 1);
    }

    [Fact]
    public async Task ReAudit_Offline_ScopedWordCountMetricRederived()
    {
        await using var origin = await ContentSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (auditId, baselineRequests) = await CrawlAsync(backend, origin,
            customize: c => c with { ContentScope = new ContentScopeDefinition([], ["nav", "footer"]) });

        var sourceMetrics = await backend.ListByAuditAsync<MetricSummaryDto>(auditId);
        var sourceScoped = sourceMetrics.Where(m => m.MetricKey == MetricKeys.ScopedWordCount).ToList();
        Assert.NotEmpty(sourceScoped);
        // The boilerplate-bearing page (/p/1) actually had words removed by the
        // scope; chrome-free pages legitimately remove nothing.
        Assert.Contains(sourceScoped, m =>
        {
            using var dimensions = JsonDocument.Parse(m.DimensionsJson);
            return dimensions.RootElement.GetProperty("removedWordCount").GetInt64() > 0;
        });

        var reAuditId = await backend.Service<IAuditLifecyclePort>().ReAuditAsync(auditId, refresh: false);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // Scoped metrics re-derived from stored bodies, offline, same values.
        Assert.Equal(baselineRequests, origin.RequestCount);
        var reMetrics = await backend.ListByAuditAsync<MetricSummaryDto>(reAuditId);
        var reScoped = reMetrics.Where(m => m.MetricKey == MetricKeys.ScopedWordCount).ToList();
        Assert.Equal(sourceScoped.Count, reScoped.Count);
        Assert.Equal(
            sourceScoped.Select(m => m.Value).OrderBy(v => v),
            reScoped.Select(m => m.Value).OrderBy(v => v));

        // The content_scope diagnostics element group is re-extracted too
        // (url_elements filters by urlId, not auditId).
        var reUrls = await backend.ListByAuditAsync<UrlRecordDto>(reAuditId);
        var elements = backend.Service<IReadRepository<UrlElementDto>>();
        var scopeElements = new List<UrlElementDto>();
        foreach (var url in reUrls)
        {
            var page = await elements.ListAsync(new ListQuery(0, 200,
                new Dictionary<string, string> { ["urlId"] = url.Id.ToString() }));
            scopeElements.AddRange(page.Items.Where(e => e.ElementGroup == ElementGroups.ContentScope));
        }
        Assert.NotEmpty(scopeElements);
    }

    [Fact]
    public async Task ReAudit_RefreshTrue_RefetchesFromOrigin()
    {
        await using var origin = await ContentSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (auditId, baselineRequests) = await CrawlAsync(backend, origin);

        var reAuditId = await backend.Service<IAuditLifecyclePort>().ReAuditAsync(auditId, refresh: true);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // Explicit refresh is the ONLY re-fetch path (§7.2).
        Assert.True(origin.RequestCount > baselineRequests,
            $"Refresh re-audit should re-fetch: baseline {baselineRequests}, now {origin.RequestCount}.");
    }

    [Fact]
    public async Task LiveAudit_CustomExtractionRules_PersistExtractedContent()
    {
        // Criterion #31 live facet: configured extraction rules must run during
        // a real audit and persist extracted_content rows. Flips green when
        // defect D5 (custom family never invoked by the engine) is fixed.
        await using var origin = await ContentSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        // The engine's tolerant parser reads numeric enum values (Web defaults).
        var rulesJson = JsonSerializer.Serialize(new[]
        {
            new { name = "sku", selector = ".sku", valueType = 0, allowMultiple = false }
        });
        var (auditId, _) = await CrawlAsync(backend, origin,
            customize: c => c with { CustomExtractionRulesJson = rulesJson });

        var extractions = await backend.ListByAuditAsync<ExtractedContentDto>(auditId);
        Assert.True(extractions.Count > 0,
            "Live audit persisted no extracted_content rows: the custom extraction family is not invoked by the engine (D5).");
        Assert.Contains(extractions, e => e.RuleName == "sku" && e.TypedValueJson!.Contains("SKU-42"));
    }

    [Fact]
    public async Task RestReaudit_RefreshFalse_StaysOffline()
    {
        // Criterion #15 over the REST surface: refresh:false routes through
        // the reaudit port and re-analyzes stored artifacts with zero network
        // (D2 regression guard — the controller once always re-fetched).
        await using var origin = await ContentSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (auditId, baselineRequests) = await CrawlAsync(backend, origin);

        var response = await backend.Http.PostAsJsonAsync($"/api/audits/{auditId}/reaudit",
            new { refresh = false }, WireOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"REST reaudit failed: {(int)response.StatusCode} {body}");
        var envelope = JsonDocument.Parse(body).RootElement;
        var reAuditId = long.Parse(envelope.GetProperty("data").GetProperty("auditId").GetString()!);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        Assert.Equal(baselineRequests, origin.RequestCount);
    }
}