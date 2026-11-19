using System.Net.Http.Json;
using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.ListMode;
using CrawlForge.E2E.Tests.Fixtures;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// List Mode ingestion, discovery modes, and the five-tier migration redirect
/// matrix over real loopback origins (WP-07-01, criterion #43): list seeds
/// persist with discovery source <c>list</c> at depth 0, list-only disables
/// discovery, list-bounded skips off-list discoveries with reason
/// <c>out-of-list</c>, and the matrix emits the correct verdict among the five
/// <c>redirect-matrix.*</c> tiers with hop-chain evidence, findings, and
/// verdict-dimensioned aggregates.
/// </summary>
public sealed class ListModeE2ETests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Index with no outbound links: the crawl fetches only the seed.</summary>
    private static Task<LoopbackOrigin> QuietIndexAsync() =>
        LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page("Index", "<p>Quiet index with no links.</p>")));
            routes.Map("/a", OriginResponse.Html(FixturePages.Page("A", "<p>Page A content.</p>")));
            routes.Map("/b", OriginResponse.Html(FixturePages.Page("B", "<p>Page B content.</p>")));
        });

    private static string PageWithCanonical(string title, string canonicalPath) =>
        $"<!doctype html><html lang=\"en\"><head><title>{title}</title>" +
        $"<link rel=\"canonical\" href=\"{canonicalPath}\"></head>" +
        $"<body><p>{title} body content.</p></body></html>";

    /// <summary>
    /// One fixture per verdict tier: pass (301→canonical-matching target),
    /// target-mismatch (302→different target), chain-exceeded (307/308 loop),
    /// final-non-200 (303→404), canonical-mismatch (301→target whose canonical
    /// points elsewhere).
    /// </summary>
    private static Task<LoopbackOrigin> MatrixOriginAsync() =>
        LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            routes.Map("/", OriginResponse.Html(FixturePages.Page("Index", "<p>Matrix fixture index.</p>")));

            routes.Map("/ok-source", OriginResponse.Redirect("/ok-target", 301));
            routes.Map("/ok-target", OriginResponse.Html(PageWithCanonical("OK Target", "/ok-target")));

            routes.Map("/wrong-source", OriginResponse.Redirect("/actual-target", 302));
            routes.Map("/actual-target", OriginResponse.Html(PageWithCanonical("Actual Target", "/actual-target")));

            routes.Map("/loop-a", OriginResponse.Redirect("/loop-b", 307));
            routes.Map("/loop-b", OriginResponse.Redirect("/loop-a", 308));

            routes.Map("/gone-source", OriginResponse.Redirect("/gone", 303));
            routes.Map("/gone", OriginResponse.Status(404, "<html><body>Gone</body></html>"));

            routes.Map("/canon-source", OriginResponse.Redirect("/canon-target", 301));
            routes.Map("/canon-target", OriginResponse.Html(PageWithCanonical("Canon Target", "/elsewhere")));
        });

    private static RedirectMappingDto[] MatrixRows(long auditId, string baseUrl) =>
    [
        new(0, auditId, baseUrl + "ok-source", baseUrl + "ok-target", 0),
        new(0, auditId, baseUrl + "wrong-source", baseUrl + "expected-target", 1),
        new(0, auditId, baseUrl + "loop-a", baseUrl + "loop-target", 2),
        new(0, auditId, baseUrl + "gone-source", baseUrl + "gone-target", 3),
        new(0, auditId, baseUrl + "canon-source", baseUrl + "canon-target", 4)
    ];

    [Fact]
    public async Task Ingestion_ValidatesDedupesAndPersistsListSeeds()
    {
        await using var origin = await QuietIndexAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var ingestor = backend.Service<IListModeIngestor>();
        var result = await ingestor.IngestAsync(auditId,
        [
            origin.BaseUrl + "a",
            origin.BaseUrl + "b",
            origin.BaseUrl + "a",          // duplicate within the list
            "not a url",                    // malformed → rejected, never fails the audit
            "ftp://invalid.example/x"       // non-http(s) scheme → rejected
        ], allowDiscovery: false);

        Assert.Equal(2, result.IngestedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(2, result.InvalidCount);

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        var listSeeds = urls.Where(u => u.DiscoverySource == DiscoverySource.List).ToList();
        Assert.Equal(2, listSeeds.Count);
        Assert.All(listSeeds, u =>
        {
            Assert.Equal(0, u.Depth);
            Assert.Equal(CrawlStatusClass.NotCrawled, u.StatusClass);
            Assert.Equal("127.0.0.1", u.Host);
        });
        Assert.Contains(listSeeds, u => u.CanonicalUrl.EndsWith("/a", StringComparison.Ordinal));
        Assert.Contains(listSeeds, u => u.CanonicalUrl.EndsWith("/b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ingestion_ReingestionDeduplicatesAgainstPersistedRecords()
    {
        await using var origin = await QuietIndexAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var ingestor = backend.Service<IListModeIngestor>();
        var first = await ingestor.IngestAsync(auditId, [origin.BaseUrl + "a", origin.BaseUrl + "b"], false);
        Assert.Equal(2, first.IngestedCount);

        var second = await ingestor.IngestAsync(auditId, [origin.BaseUrl + "a", origin.BaseUrl + "b"], false);
        Assert.Equal(0, second.IngestedCount);
        Assert.Equal(2, second.DuplicateCount);

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        Assert.Equal(2, urls.Count(u => u.DiscoverySource == DiscoverySource.List));
    }

    [Fact]
    public async Task ListOnlyPolicy_ForcesDepthZeroAndDisablesDiscovery()
    {
        await using var origin = await QuietIndexAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var ingestor = Assert.IsType<ListModeIngestor>(backend.Service<IListModeIngestor>());
        await ingestor.IngestAsync(auditId, [origin.BaseUrl + "a"], false);

        var policy = await ingestor.ResolvePolicyAsync(auditId, allowDiscovery: false);
        Assert.Equal(ListDiscoveryMode.ListOnly, policy.Mode);
        Assert.Equal(0, policy.DepthCeiling);
        Assert.False(policy.DiscoveryEnabled);
        Assert.Contains(policy.AllowedCanonicalUrls, u => u.EndsWith("/a", StringComparison.Ordinal));

        var gate = new ListDiscoveryGate(policy);
        // Seeds and list entries always pass.
        Assert.True(gate.Decide(new FrontierEntry(origin.BaseUrl + "a", "127.0.0.1", 0, 1.0, DiscoverySource.List)).Enqueue);
        Assert.True(gate.Decide(new FrontierEntry(origin.BaseUrl, "127.0.0.1", 0, 1.0, DiscoverySource.Seed)).Enqueue);
        // Every link discovery is disabled in list-only mode — even on-list URLs.
        var decision = gate.Decide(new FrontierEntry(origin.BaseUrl + "a", "127.0.0.1", 1, 0.5, DiscoverySource.Link));
        Assert.False(decision.Enqueue);
        Assert.Equal(ListDiscoveryGate.DiscoveryDisabledReason, decision.SkipReason);
        Assert.Equal(1, gate.SkipCount);
    }

    [Fact]
    public async Task ListBoundedPolicy_EnqueuesOnListAndSkipsOffListDiscoveries()
    {
        await using var origin = await QuietIndexAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var ingestor = Assert.IsType<ListModeIngestor>(backend.Service<IListModeIngestor>());
        await ingestor.IngestAsync(auditId, [origin.BaseUrl + "a", origin.BaseUrl + "b"], false);

        var policy = await ingestor.ResolvePolicyAsync(auditId, allowDiscovery: true);
        Assert.Equal(ListDiscoveryMode.ListBounded, policy.Mode);
        Assert.True(policy.DiscoveryEnabled);
        Assert.Equal(2, policy.AllowedCanonicalUrls.Count);

        var gate = new ListDiscoveryGate(policy);
        var onList = policy.AllowedCanonicalUrls.First(u => u.EndsWith("/a", StringComparison.Ordinal));

        // On-list discovery within the depth ceiling enqueues.
        Assert.True(gate.Decide(new FrontierEntry(onList, "127.0.0.1", 1, 0.5, DiscoverySource.Link)).Enqueue);
        // Off-list discovery is skipped with reason out-of-list and recorded.
        var offList = gate.Decide(new FrontierEntry(origin.BaseUrl + "off-list", "127.0.0.1", 1, 0.5, DiscoverySource.Link));
        Assert.False(offList.Enqueue);
        Assert.Equal(ListDiscoveryGate.OutOfListReason, offList.SkipReason);
        Assert.Equal(1, gate.OutOfListSkipCount);
        Assert.Contains(gate.Skips, s => s.Reason == ListDiscoveryGate.OutOfListReason && s.Url.EndsWith("/off-list", StringComparison.Ordinal));
        // Depth ceiling still applies to on-list discoveries.
        var tooDeep = gate.Decide(new FrontierEntry(onList, "127.0.0.1", policy.DepthCeiling + 1, 0.5, DiscoverySource.Link));
        Assert.False(tooDeep.Enqueue);
        Assert.Equal(ListDiscoveryGate.DepthCeilingReason, tooDeep.SkipReason);
    }

    [Fact]
    public async Task RestStart_ListMode_SnapshotsTheListAndValidatesInput()
    {
        await using var origin = await QuietIndexAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (_, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        // Valid list-mode start: 201 + frozen list snapshot on the audit row.
        var start = await backend.Http.PostAsJsonAsync("/api/audits", new
        {
            configurationId = configurationId.ToString(),
            listMode = true,
            urlList = new[] { origin.BaseUrl + "a", origin.BaseUrl + "b" }
        }, WireOptions);
        Assert.Equal(System.Net.HttpStatusCode.Created, start.StatusCode);
        var startEnvelope = await WireJson.EnvelopeAsync(start);
        // Phase-10 step 0: the locked REST envelope carries auditId as a STRING
        // EntityId — assert the kind explicitly, never tolerate a numeric id.
        var startIdToken = startEnvelope.GetProperty("data").GetProperty("auditId");
        Assert.Equal(JsonValueKind.String, startIdToken.ValueKind);
        var newAuditId = long.Parse(startIdToken.GetString()!);

        var audit = await backend.Service<IReadRepository<AuditDto>>().GetByIdAsync(newAuditId);
        Assert.NotNull(audit);
        Assert.True(audit!.IsListMode);
        Assert.NotNull(audit.UrlListJson);
        Assert.Contains("/a", audit.UrlListJson!);
        Assert.Contains("/b", audit.UrlListJson!);
        await backend.WaitForStatusAsync(newAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // listMode=true without a list → 400 with a urlList field error.
        var missingList = await backend.Http.PostAsJsonAsync("/api/audits", new
        {
            configurationId = configurationId.ToString(),
            listMode = true
        }, WireOptions);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missingList.StatusCode);
        var missingEnvelope = await WireJson.EnvelopeAsync(missingList);
        Assert.Equal("validation", missingEnvelope.GetProperty("error").GetProperty("code").GetString());
        // The frozen wire guard renders fieldErrors as a field → reasons map.
        Assert.True(missingEnvelope.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("urlList", out _));

        // Malformed list entry → 400 with a per-index field error.
        var invalidEntry = await backend.Http.PostAsJsonAsync("/api/audits", new
        {
            configurationId = configurationId.ToString(),
            urlList = new[] { "relative/not-absolute" }
        }, WireOptions);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidEntry.StatusCode);
        var invalidEnvelope = await WireJson.EnvelopeAsync(invalidEntry);
        Assert.True(invalidEnvelope.GetProperty("error").GetProperty("fieldErrors").TryGetProperty("urlList[0]", out _));
    }

    [Fact]
    public async Task RedirectMatrix_FiveTierVerdicts_PersistFindingsAndIdempotentAggregates()
    {
        await using var origin = await MatrixOriginAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // List-seed the matrix sources so findings carry URL references.
        var ingestor = backend.Service<IListModeIngestor>();
        var rows = MatrixRows(auditId, origin.BaseUrl);
        var ingestion = await ingestor.IngestAsync(auditId, rows.Select(r => r.SourceUrl).ToList(), false);
        Assert.Equal(5, ingestion.IngestedCount);

        // The accepted validator component composed from the locked contracts.
        var validator = new RedirectMatrixValidator(
            backend.Service<IHttpFetcher>(),
            backend.Service<IDbProvider>(),
            backend.Service<IUrlCanonicalizer>(),
            backend.Service<Contracts.Api.IRealtimeHub>());

        var results = await validator.ValidateAsync(auditId, rows);

        // Verdict precedence tiers in ordinal order.
        Assert.Equal(
            [RedirectMatrixVerdict.Pass, RedirectMatrixVerdict.TargetMismatch, RedirectMatrixVerdict.ChainExceeded,
             RedirectMatrixVerdict.FinalNon200, RedirectMatrixVerdict.CanonicalMismatch],
            results.Select(r => r.Verdict).ToArray());

        // Hop-chain evidence: the pass row records its single 301 hop; the loop
        // row records both hops before the loop is detected.
        Assert.Equal(1, results[0].HopCount);
        Assert.Equal(301, results[0].Chain[0].StatusCode);
        Assert.Equal(origin.BaseUrl + "ok-target", results[0].ActualFinalUrl);
        Assert.Equal(2, results[2].HopCount);
        Assert.Equal(307, results[2].Chain[0].StatusCode);
        Assert.Equal(308, results[2].Chain[1].StatusCode);
        Assert.Equal(303, results[3].Chain[0].StatusCode);
        Assert.EndsWith("/elsewhere", results[4].CanonicalUrl!);

        // Findings: one per row under the five additive rule keys, with the
        // §10.3 severities (Error/Error/Error/Warning/Pass in tier order).
        var findings = await backend.ListByAuditAsync<FindingDto>(auditId);
        var matrixFindings = findings.Where(f => f.RuleKey.StartsWith("redirect-matrix.", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, matrixFindings.Count);
        Assert.Contains(matrixFindings, f => f.RuleKey == FindingRuleKeys.RedirectMatrixPass && f.Severity == FindingSeverity.Pass);
        Assert.Contains(matrixFindings, f => f.RuleKey == FindingRuleKeys.RedirectMatrixTargetMismatch && f.Severity == FindingSeverity.Error);
        Assert.Contains(matrixFindings, f => f.RuleKey == FindingRuleKeys.RedirectMatrixChainExceeded && f.Severity == FindingSeverity.Error);
        Assert.Contains(matrixFindings, f => f.RuleKey == FindingRuleKeys.RedirectMatrixFinalNon200 && f.Severity == FindingSeverity.Error);
        Assert.Contains(matrixFindings, f => f.RuleKey == FindingRuleKeys.RedirectMatrixCanonicalMismatch && f.Severity == FindingSeverity.Warning);
        Assert.All(matrixFindings, f => Assert.NotNull(f.UrlId));

        // Aggregates: five verdict dimensions, one row each; re-validation is
        // idempotent (delete + insert, never double-counted).
        var aggregates = await RedirectMatrixResults.ReadAggregatesAsync(backend.Service<IDbProvider>(), auditId);
        Assert.Equal(5, aggregates.Count);
        Assert.All(aggregates, a => Assert.Equal(1, a.Count));

        await validator.ValidateAsync(auditId, rows);
        var refreshed = await RedirectMatrixResults.ReadAggregatesAsync(backend.Service<IDbProvider>(), auditId);
        Assert.All(refreshed, a => Assert.Equal(1, a.Count));
    }

    [Fact]
    public async Task RedirectMatrix_Revalidation_DoesNotDuplicateFindings()
    {
        // §10.3: one primary verdict per row. The REST results endpoint
        // re-validates on every read, so finding persistence must be
        // idempotent like the aggregate refresh. Flips green when defect D4
        // (INSERT-only finding persistence) is fixed.
        await using var origin = await MatrixOriginAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        var validator = new RedirectMatrixValidator(
            backend.Service<IHttpFetcher>(),
            backend.Service<IDbProvider>(),
            backend.Service<IUrlCanonicalizer>(),
            backend.Service<Contracts.Api.IRealtimeHub>());
        var rows = MatrixRows(auditId, origin.BaseUrl);

        await validator.ValidateAsync(auditId, rows);
        await validator.ValidateAsync(auditId, rows);

        var findings = await backend.ListByAuditAsync<FindingDto>(auditId);
        var matrixFindings = findings.Count(f => f.RuleKey.StartsWith("redirect-matrix.", StringComparison.Ordinal));
        Assert.True(matrixFindings == 5,
            $"Re-validation duplicated matrix findings: expected 5 (one per row), got {matrixFindings}.");
    }

    [Fact]
    public async Task RedirectMatrix_RestUploadAndResults_ValidateFiveTiers()
    {
        await using var origin = await MatrixOriginAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // §10.7 upload with auto-seeding: rows persist and a list-mode audit is
        // seeded from the Source column.
        var upload = await backend.Http.PostAsJsonAsync($"/api/audits/{auditId}/redirect-matrix", new
        {
            rows = new[]
            {
                new { sourceUrl = origin.BaseUrl + "ok-source", targetUrl = origin.BaseUrl + "ok-target" },
                new { sourceUrl = origin.BaseUrl + "wrong-source", targetUrl = origin.BaseUrl + "expected-target" },
                new { sourceUrl = origin.BaseUrl + "loop-a", targetUrl = origin.BaseUrl + "loop-target" },
                new { sourceUrl = origin.BaseUrl + "gone-source", targetUrl = origin.BaseUrl + "gone-target" },
                new { sourceUrl = origin.BaseUrl + "canon-source", targetUrl = origin.BaseUrl + "canon-target" }
            },
            autoSeedAudit = true
        }, WireOptions);
        var uploadBody = await upload.Content.ReadAsStringAsync();
        Assert.True(upload.IsSuccessStatusCode, $"Matrix upload failed: {(int)upload.StatusCode} {uploadBody}");
        var uploadEnvelope = JsonDocument.Parse(uploadBody).RootElement;
        Assert.Equal(5, uploadEnvelope.GetProperty("data").GetProperty("rowCount").GetInt32());
        // Phase-10 step 0: string EntityId lock (see the start-envelope assert).
        var seededIdToken = uploadEnvelope.GetProperty("data").GetProperty("auditId");
        Assert.Equal(JsonValueKind.String, seededIdToken.ValueKind);
        var seededAuditId = long.Parse(seededIdToken.GetString()!);
        await backend.WaitForStatusAsync(seededAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // The auto-seeded audit carries the five sources as list seeds.
        var seededUrls = await backend.ListByAuditAsync<UrlRecordDto>(seededAuditId);
        Assert.True(seededUrls.Count(u => u.DiscoverySource == DiscoverySource.List) == 5,
            $"Expected 5 list seeds in audit {seededAuditId}, got {seededUrls.Count(u => u.DiscoverySource == DiscoverySource.List)}. " +
            $"Rows: {string.Join("; ", seededUrls.Select(u => $"{u.CanonicalUrl} [{u.DiscoverySource}/{u.StatusClass}]"))}");

        // §10.7 results: the matrix is audit-scoped to the UPLOADING audit;
        // per-row verdicts with hop chains and canonical evidence.
        var results = await backend.Http.GetAsync($"/api/audits/{auditId}/redirect-matrix/results?limit=50");
        var resultsBody = await results.Content.ReadAsStringAsync();
        Assert.True(results.IsSuccessStatusCode, $"Matrix results failed: {(int)results.StatusCode} {resultsBody}");
        var envelope = JsonDocument.Parse(resultsBody).RootElement;
        Assert.Equal(5, envelope.GetProperty("pagination").GetProperty("total").GetInt32());

        var verdicts = envelope.GetProperty("data").EnumerateArray()
            .ToDictionary(r => r.GetProperty("sourceUrl").GetString()!, r => r.GetProperty("verdict").GetString()!);
        // Verdict vocabulary follows the spec's §10.3 names (criterion #43):
        // pass / target-mismatch / chain-exceeded / final-non-200 /
        // canonical-mismatch (WP-09-03 MatrixVerdictNames; the REST verdict
        // filter validates the same hyphenated vocabulary).
        Assert.Equal("pass", verdicts[origin.BaseUrl + "ok-source"]);
        Assert.Equal("target-mismatch", verdicts[origin.BaseUrl + "wrong-source"]);
        Assert.Equal("chain-exceeded", verdicts[origin.BaseUrl + "loop-a"]);
        Assert.Equal("final-non-200", verdicts[origin.BaseUrl + "gone-source"]);
        Assert.Equal("canonical-mismatch", verdicts[origin.BaseUrl + "canon-source"]);

        // Verdict filter narrows the projection.
        var filtered = await backend.Http.GetAsync($"/api/audits/{auditId}/redirect-matrix/results?verdict=pass");
        var filteredEnvelope = await WireJson.EnvelopeAsync(filtered);
        Assert.Equal(1, filteredEnvelope.GetProperty("pagination").GetProperty("total").GetInt32());
    }
}