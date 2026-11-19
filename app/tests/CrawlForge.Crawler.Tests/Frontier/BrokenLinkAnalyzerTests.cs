using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.Tests.Frontier.Support;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// Broken internal/external link findings (CAP-B-046/047; WP-03-04 reopen:
/// external DNS/connect failures were never reported). Real SQLite graph +
/// scripted external probes; source-URL evidence, bounds, and
/// producer→consumer persistence.
/// </summary>
public sealed class BrokenLinkAnalyzerTests
{
    private const long AuditId = 1;
    private const string Base = "http://site.example";
    private const string ExternalBad = "https://broken.example.invalid/page";
    private const string External404 = "https://gone.example.org/missing";
    private const string ExternalOk = "https://healthy.example.org/";

    private sealed class ScriptedProbeFetcher : IHttpFetcher
    {
        private readonly Dictionary<string, Func<HttpFetchResult>> _routes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Probed { get; } = new();

        public ScriptedProbeFetcher Route(string url, int status)
        {
            _routes[url] = () => new HttpFetchResult(url, status, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero);
            return this;
        }

        public ScriptedProbeFetcher Fail(string url, FetchFailureKind kind)
        {
            _failures[url] = new FetchFailedException(kind, $"simulated {kind}");
            return this;
        }

        public Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
        {
            lock (Probed)
            {
                Probed.Add(request.Url);
            }

            if (_failures.TryGetValue(request.Url, out var failure))
            {
                throw failure;
            }

            if (_routes.TryGetValue(request.Url, out var factory))
            {
                return Task.FromResult(factory());
            }

            throw new InvalidOperationException($"No scripted probe for '{request.Url}'.");
        }
    }

    private static async Task<FrontierTestDatabase> SeedInternalGraphAsync()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await database.SeedAuditAsync(AuditId);
        var source = await database.InsertUrlAsync(AuditId, Base + "/pages/broken-links.html", DiscoverySource.Link, CrawlStatusClass.Success, 200);
        var notFound = await database.InsertUrlAsync(AuditId, Base + "/pages/does-not-exist.html", DiscoverySource.Link, CrawlStatusClass.ClientError, 404);
        var serverError = await database.InsertUrlAsync(AuditId, Base + "/pages/error.html", DiscoverySource.Link, CrawlStatusClass.ServerError, 500);
        var fetchError = await database.InsertUrlAsync(AuditId, Base + "/pages/dead.html", DiscoverySource.Link, CrawlStatusClass.FetchError, null);
        var unreachable = await database.InsertUrlAsync(AuditId, Base + "/pages/no-inlinks.html", DiscoverySource.Sitemap, CrawlStatusClass.ClientError, 404);
        var healthy = await database.InsertUrlAsync(AuditId, Base + "/pages/ok.html", DiscoverySource.Link, CrawlStatusClass.Success, 200);
        await database.InsertLinkEdgeAsync(AuditId, source, Base + "/pages/does-not-exist.html", notFound, isInternal: true);
        await database.InsertLinkEdgeAsync(AuditId, source, Base + "/pages/error.html", serverError, isInternal: true);
        await database.InsertLinkEdgeAsync(AuditId, source, Base + "/pages/dead.html", fetchError, isInternal: true);
        await database.InsertLinkEdgeAsync(AuditId, source, Base + "/pages/ok.html", healthy, isInternal: true);
        // unreachable has NO in-links: not a broken-link finding (orphan territory).
        _ = unreachable;
        return database;
    }

    [Fact]
    public async Task Analyze_InternalErrorTargetsWithInlinks_BrokenInternalFindings()
    {
        var database = await SeedInternalGraphAsync();
        await using var _ = database;
        var analyzer = new BrokenLinkAnalyzer(database.Provider, new ScriptedProbeFetcher());

        BrokenLinkAnalysisResult result = await analyzer.AnalyzeAsync(AuditId, new BrokenLinkOptions(CheckExternalLinks: false));

        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f =>
        {
            Assert.Equal(LinkRuleKeys.BrokenInternal, f.RuleKey);
            Assert.Equal(AuditCategory.Linking, f.Category);
            Assert.Equal(FindingSeverity.Error, f.Severity);
        });

        FindingDto notFound = result.Findings.Single(f => f.EvidenceJson.Contains("does-not-exist"));
        using var evidence = JsonDocument.Parse(notFound.EvidenceJson);
        Assert.Equal(404, evidence.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("ClientError", evidence.RootElement.GetProperty("statusClass").GetString());
        Assert.Equal(Base + "/pages/broken-links.html", evidence.RootElement.GetProperty("sourceUrls")[0].GetString());
        Assert.Equal(1, evidence.RootElement.GetProperty("sourceCount").GetInt32());

        Assert.Contains(result.Findings, f => f.EvidenceJson.Contains("\"statusCode\":500"));
        Assert.Contains(result.Findings, f => f.EvidenceJson.Contains("\"statusClass\":\"FetchError\""));
        Assert.Equal(0, result.ExternalChecked);
    }

    [Fact]
    public async Task Analyze_ExternalTargets_ProbedOnceAndReportedWithSources()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var sourceA = await database.InsertUrlAsync(AuditId, Base + "/a", DiscoverySource.Seed, CrawlStatusClass.Success, 200);
        var sourceB = await database.InsertUrlAsync(AuditId, Base + "/b", DiscoverySource.Link, CrawlStatusClass.Success, 200);
        await database.InsertLinkEdgeAsync(AuditId, sourceA, ExternalBad, null, isInternal: false);
        await database.InsertLinkEdgeAsync(AuditId, sourceB, ExternalBad, null, isInternal: false); // duplicate target
        await database.InsertLinkEdgeAsync(AuditId, sourceA, External404, null, isInternal: false);
        await database.InsertLinkEdgeAsync(AuditId, sourceA, ExternalOk, null, isInternal: false);
        var fetcher = new ScriptedProbeFetcher()
            .Fail(ExternalBad, FetchFailureKind.NameResolution)
            .Route(External404, 404)
            .Route(ExternalOk, 200);
        var analyzer = new BrokenLinkAnalyzer(database.Provider, fetcher);

        BrokenLinkAnalysisResult result = await analyzer.AnalyzeAsync(AuditId);

        Assert.Equal(3, result.ExternalChecked);
        Assert.Equal(2, result.ExternalBroken);
        Assert.Equal(3, fetcher.Probed.Count); // distinct targets only
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f =>
        {
            Assert.Equal(LinkRuleKeys.BrokenExternal, f.RuleKey);
            Assert.Equal(AuditCategory.Linking, f.Category);
            Assert.Equal(FindingSeverity.Warning, f.Severity);
            Assert.Null(f.UrlId);
        });

        FindingDto dns = result.Findings.Single(f => f.EvidenceJson.Contains("broken.example.invalid"));
        using (var evidence = JsonDocument.Parse(dns.EvidenceJson))
        {
            Assert.Equal("NameResolution", evidence.RootElement.GetProperty("failureKind").GetString());
            Assert.Equal(2, evidence.RootElement.GetProperty("sourceCount").GetInt32());
            Assert.Equal(2, evidence.RootElement.GetProperty("sourceUrls").GetArrayLength());
        }

        FindingDto http404 = result.Findings.Single(f => f.EvidenceJson.Contains("gone.example.org"));
        using var statusEvidence = JsonDocument.Parse(http404.EvidenceJson);
        Assert.Equal(404, statusEvidence.RootElement.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task Analyze_ExternalCheckBound_StopsWithDiagnostic()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var source = await database.InsertUrlAsync(AuditId, Base + "/a", DiscoverySource.Seed, CrawlStatusClass.Success, 200);
        await database.InsertLinkEdgeAsync(AuditId, source, ExternalBad, null, isInternal: false);
        await database.InsertLinkEdgeAsync(AuditId, source, External404, null, isInternal: false);
        var fetcher = new ScriptedProbeFetcher()
            .Fail(ExternalBad, FetchFailureKind.NameResolution)
            .Route(External404, 404);
        var analyzer = new BrokenLinkAnalyzer(database.Provider, fetcher);

        BrokenLinkAnalysisResult result = await analyzer.AnalyzeAsync(AuditId, new BrokenLinkOptions(MaxExternalChecks: 1));

        Assert.Equal(1, result.ExternalChecked);
        Assert.Single(fetcher.Probed);
        Assert.Contains(result.Diagnostics, d => d.Contains("1-target bound"));
    }

    [Fact]
    public async Task Analyze_SourceEvidenceBound_TruncatesSourcesButCountsAll()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var target = await database.InsertUrlAsync(AuditId, Base + "/missing", DiscoverySource.Link, CrawlStatusClass.ClientError, 404);
        for (var i = 0; i < 5; i++)
        {
            var source = await database.InsertUrlAsync(AuditId, Base + $"/source-{i}", DiscoverySource.Link, CrawlStatusClass.Success, 200);
            await database.InsertLinkEdgeAsync(AuditId, source, Base + "/missing", target, isInternal: true);
        }

        var analyzer = new BrokenLinkAnalyzer(database.Provider, new ScriptedProbeFetcher());
        BrokenLinkAnalysisResult result = await analyzer.AnalyzeAsync(
            AuditId, new BrokenLinkOptions(CheckExternalLinks: false, MaxSourcesPerFinding: 2));

        FindingDto finding = Assert.Single(result.Findings);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal(2, evidence.RootElement.GetProperty("sourceUrls").GetArrayLength());
        Assert.Equal(5, evidence.RootElement.GetProperty("sourceCount").GetInt32());
    }

    [Fact]
    public async Task Analyze_FindingsPersistedAndReadBackThroughRepository()
    {
        var database = await FrontierTestDatabase.CreateAsync();
        await using var _ = database;
        await database.SeedAuditAsync(AuditId);
        var source = await database.InsertUrlAsync(AuditId, Base + "/a", DiscoverySource.Seed, CrawlStatusClass.Success, 200);
        await database.InsertLinkEdgeAsync(AuditId, source, ExternalBad, null, isInternal: false);
        var fetcher = new ScriptedProbeFetcher().Fail(ExternalBad, FetchFailureKind.NameResolution);
        var analyzer = new BrokenLinkAnalyzer(database.Provider, fetcher);
        BrokenLinkAnalysisResult result = await analyzer.AnalyzeAsync(AuditId);

        var recorder = new CrawlFindingRecorder(database.Provider);
        Assert.Equal(1, await recorder.RecordAsync(AuditId, result.Findings));

        var repository = new SqlRepository<FindingDto>(database.Provider);
        var stored = await repository.ListAsync(new ListQuery(0, 50, new Dictionary<string, string>
        {
            ["auditId"] = AuditId.ToString(),
            ["ruleKey"] = LinkRuleKeys.BrokenExternal,
        }));
        FindingDto row = Assert.Single(stored.Items);
        Assert.Null(row.UrlId);
        Assert.Contains("NameResolution", row.EvidenceJson);
        Assert.Equal(0, await recorder.RecordAsync(AuditId, result.Findings));
    }
}
