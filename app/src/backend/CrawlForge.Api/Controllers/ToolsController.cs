using System.Text;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Standalone tools (FS-CRAWL-001 §8.7 + FS-CRAWL-002 additive tools): robots
/// test, URL check, structured-data check, render test, sitemap generation,
/// scope preview, rewrite preview, and llms.txt generation/validation. All
/// network access flows through the locked <see cref="IHttpFetcher"/> boundary
/// toward user-configured targets only; generation writes local files (E9).
/// </summary>
[Route("api/tools")]
public sealed class ToolsController : ApiControllerBase
{
    private const int BodySnippetLength = 4096;

    private static readonly DeviceProfile DesktopProfile = new("desktop", 1920, 1080, "CrawlForge-Render", IsMobile: false);
    private static readonly DeviceProfile MobileProfile = new("mobile", 390, 844, "CrawlForge-Render-Mobile", IsMobile: true);
    private static readonly DeviceProfile TabletProfile = new("tablet", 768, 1024, "CrawlForge-Render-Tablet", IsMobile: true);

    private readonly IRobotsTxtParser _robots;
    private readonly IHttpFetcher _fetcher;
    private readonly IWafChallengeEvaluator _waf;
    private readonly IStructuredDataValidator _structuredData;
    private readonly ITolerantDomParser _parser;
    private readonly IRenderingPool _rendering;
    private readonly IExportEngine _exports;
    private readonly IReadRepository<AuditDto> _audits;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly ScopePreviewService _scopePreview;
    private readonly IUrlRewriteEngine _rewriter;
    private readonly ILlmsTxtGenerator _llmsGenerator;
    private readonly IGeoReadinessAnalyzer _geo;

    /// <summary>Creates the controller.</summary>
    /// <param name="robots">Robots parser (local).</param>
    /// <param name="fetcher">Locked HTTP fetch boundary.</param>
    /// <param name="waf">WAF challenge evaluator.</param>
    /// <param name="structuredData">Structured-data validator.</param>
    /// <param name="parser">Tolerant DOM parser.</param>
    /// <param name="rendering">Rendering pool.</param>
    /// <param name="exports">Export engine (sitemap generation).</param>
    /// <param name="audits">Audit read repository.</param>
    /// <param name="urls">URL read repository.</param>
    /// <param name="scopePreview">Scope preview service.</param>
    /// <param name="rewriter">URL rewrite engine.</param>
    /// <param name="llmsGenerator">llms.txt generator.</param>
    /// <param name="geo">GEO readiness analyzer (llms validation).</param>
    public ToolsController(
        IRobotsTxtParser robots,
        IHttpFetcher fetcher,
        IWafChallengeEvaluator waf,
        IStructuredDataValidator structuredData,
        ITolerantDomParser parser,
        IRenderingPool rendering,
        IExportEngine exports,
        IReadRepository<AuditDto> audits,
        IReadRepository<UrlRecordDto> urls,
        ScopePreviewService scopePreview,
        IUrlRewriteEngine rewriter,
        ILlmsTxtGenerator llmsGenerator,
        IGeoReadinessAnalyzer geo)
    {
        _robots = robots;
        _fetcher = fetcher;
        _waf = waf;
        _structuredData = structuredData;
        _parser = parser;
        _rendering = rendering;
        _exports = exports;
        _audits = audits;
        _urls = urls;
        _scopePreview = scopePreview;
        _rewriter = rewriter;
        _llmsGenerator = llmsGenerator;
        _geo = geo;
    }

    /// <summary>Tests URLs against a robots.txt body or fetched URL.</summary>
    /// <param name="request">Robots test request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-URL allow/disallow verdicts.</returns>
    [HttpPost("robots-test")]
    public async Task<IActionResult> RobotsTest([FromBody] RobotsTestRequest request, CancellationToken cancellationToken)
    {
        if (request.TestUrls.Count == 0)
        {
            return ValidationError("At least one test URL is required.", [new ApiFieldError("testUrls", "Provide one or more URLs to test.")]);
        }

        if (string.IsNullOrWhiteSpace(request.UserAgent))
        {
            return ValidationError("A user-agent token is required.", [new ApiFieldError("userAgent", "Provide the user-agent to match.")]);
        }

        if (!string.IsNullOrWhiteSpace(request.RobotsBody) == !string.IsNullOrWhiteSpace(request.RobotsUrl))
        {
            return ValidationError("Provide exactly one of robotsBody or robotsUrl.", [new ApiFieldError("robotsBody", "robotsBody and robotsUrl are mutually exclusive.")]);
        }

        string body;
        if (!string.IsNullOrWhiteSpace(request.RobotsUrl))
        {
            var fetch = await _fetcher.FetchAsync(new HttpFetchRequest(request.RobotsUrl), cancellationToken);
            body = Encoding.UTF8.GetString(fetch.Body.Span);
        }
        else
        {
            body = request.RobotsBody!;
        }

        var document = _robots.Parse(body);
        var results = request.TestUrls
            .Select(url =>
            {
                var verdict = _robots.Evaluate(document, url, request.UserAgent);
                return new RobotsTestResultWire(url, verdict.Allowed, verdict.MatchedRule?.PathPattern);
            })
            .ToList();
        return OkEnvelope(new RobotsTestResponse(results));
    }

    /// <summary>Checks a URL's status, redirect chain, headers, and indexability.</summary>
    /// <param name="request">URL check request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The URL-check verdict with additive WAF detection.</returns>
    [HttpPost("url-check")]
    public async Task<IActionResult> UrlCheck([FromBody] UrlCheckRequest request, CancellationToken cancellationToken)
    {
        if (!IsAbsoluteUrl(request.Url))
        {
            return ValidationError("A valid absolute URL is required.", [new ApiFieldError("url", "Provide an http(s) URL.")]);
        }

        var fetch = await _fetcher.FetchAsync(new HttpFetchRequest(request.Url), cancellationToken);
        var chain = new List<RedirectHopWire>();
        if (!string.Equals(fetch.FinalUrl, request.Url, StringComparison.OrdinalIgnoreCase))
        {
            chain.Add(new RedirectHopWire(request.Url, fetch.StatusCode, fetch.FinalUrl));
        }

        var indexable = fetch.StatusCode is >= 200 and < 300 && !HasNoindexDirective(fetch.Headers);
        var snippet = Encoding.UTF8.GetString(fetch.Body.Span[..Math.Min(BodySnippetLength, fetch.Body.Length)]);
        var verdict = _waf.Evaluate(new WafEvaluationInput(fetch.FinalUrl, fetch.StatusCode, fetch.Headers, snippet));

        return OkEnvelope(new UrlCheckResponse(
            fetch.StatusCode,
            HttpStatusClass(fetch.StatusCode),
            chain,
            fetch.Headers,
            indexable,
            verdict.IsChallenge ? AuditWireMapper.ToWafVerdict(verdict) : null,
            null));
    }

    /// <summary>Validates structured data in markup or a fetched URL.</summary>
    /// <param name="request">Structured-data check request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed blocks with validation outcomes.</returns>
    [HttpPost("structured-data-check")]
    public async Task<IActionResult> StructuredDataCheck([FromBody] StructuredDataCheckRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Markup) == !string.IsNullOrWhiteSpace(request.Url))
        {
            return ValidationError("Provide exactly one of markup or url.", [new ApiFieldError("markup", "markup and url are mutually exclusive.")]);
        }

        var markup = request.Markup;
        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            var fetch = await _fetcher.FetchAsync(new HttpFetchRequest(request.Url), cancellationToken);
            markup = Encoding.UTF8.GetString(fetch.Body.Span);
        }

        var result = _structuredData.Validate(markup!, request.Url);
        var blocks = result.Blocks
            .Select(b => new StructuredDataBlockWire(
                b.Format,
                b.Type ?? string.Empty,
                new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal),
                b.IsValid,
                b.RichResultEligible,
                b.Errors))
            .ToList();
        return OkEnvelope(new StructuredDataCheckResponse(blocks));
    }

    /// <summary>Renders a URL and returns screenshot, DOM, and console capture.</summary>
    /// <param name="request">Render test request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The render-test result with additive WAF/picker fields.</returns>
    [HttpPost("render-test")]
    public async Task<IActionResult> RenderTest([FromBody] RenderTestRequest request, CancellationToken cancellationToken)
    {
        if (!IsAbsoluteUrl(request.Url))
        {
            return ValidationError("A valid absolute URL is required.", [new ApiFieldError("url", "Provide an http(s) URL.")]);
        }

        var profile = (request.DeviceProfile ?? "desktop").ToLowerInvariant() switch
        {
            "desktop" => DesktopProfile,
            "mobile" => MobileProfile,
            "tablet" => TabletProfile,
            _ => null,
        };
        if (profile is null)
        {
            return ValidationError($"Unknown device profile '{request.DeviceProfile}'.", [new ApiFieldError("deviceProfile", "Use desktop, mobile, or tablet.")]);
        }

        var page = await _rendering.RenderAsync(new RenderRequest(request.Url, profile, CaptureConsole: true), cancellationToken);
        var console = page.ConsoleMessages
            .Select(m => new ConsoleMessageWire(m.Level, m.Text, m.Timestamp.ToString("O")))
            .ToList();
        return OkEnvelope(new RenderTestResponse(
            Convert.ToBase64String(page.Screenshot.Span),
            page.Html,
            console,
            page.FinalUrl,
            page.PostRenderWafVerdict is { IsChallenge: true } verdict ? AuditWireMapper.ToWafVerdict(verdict) : null,
            request.Picker ?? false,
            // WP-10-A2-style wire-completion (WP-10-04): the locked TS mirror
            // already carries networkWaterfall (WP-10-A1); the C# response +
            // mapper complete the pair. CR-006: rendered traffic is captured
            // directly by the CDP layer, never the loopback proxy.
            page.NetworkWaterfall));
    }

    /// <summary>Generates an XML sitemap from an audit's eligible URLs (local file).</summary>
    /// <param name="request">Sitemap generation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated file name and eligible URL count.</returns>
    [HttpPost("sitemap-generate")]
    public async Task<IActionResult> SitemapGenerate([FromBody] SitemapGenerateRequest request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.AuditId, out var auditId) || await _audits.GetByIdAsync(auditId, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{request.AuditId}' was not found.");
        }

        var eligible = await _urls.ListAsync(new ListQuery(0, 1, new Dictionary<string, string>
        {
            ["auditId"] = auditId.ToString(),
            ["isIndexable"] = "true",
            ["statusClass"] = ((int)CrawlStatusClass.Success).ToString(),
        }), cancellationToken);

        var job = await _exports.CreateExportAsync(new ExportRequest(auditId, ExportFormat.XmlSitemap, ["sitemap"]), cancellationToken);
        if (job.Status != ExportJobStatus.Completed || job.FilePath is null)
        {
            return InvalidStateError("The sitemap export has not completed yet; retry once the job completes.");
        }

        return OkEnvelope(new SitemapGenerateResponse(Path.GetFileName(job.FilePath), eligible.TotalCount));
    }

    /// <summary>Previews a content scope against a sample document.</summary>
    /// <param name="request">Scope preview request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matched regions, word counts, sample text, and diagnostics.</returns>
    [HttpPost("scope-preview")]
    public IActionResult ScopePreview([FromBody] ScopePreviewRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SampleHtml))
        {
            return ValidationError("A sample HTML document is required.", [new ApiFieldError("sampleHtml", "Provide the document to preview against.")]);
        }

        var document = _parser.Parse(request.SampleHtml, "about:blank");
        ContentScopeDefinition? scope = request.ContentScope is null
            ? null
            : new ContentScopeDefinition(request.ContentScope.IncludeSelectors, request.ContentScope.ExcludeSelectors);
        var preview = _scopePreview.Preview(document, scope);

        var regions = preview.Regions
            .Select(r => new MatchedRegionWire(r.Selector, LanguageWire(r.Language), r.TagName, r.TextSample, r.WordCount))
            .ToList();
        var counts = preview.PerSelectorCounts
            .Select(c => new SelectorMatchCountWire(c.Expression, LanguageWire(c.Language), c.MatchCount))
            .ToList();
        return OkEnvelope(new ScopePreviewResponse(
            regions,
            preview.FullWordCount,
            preview.ScopedWordCount,
            preview.RemovedWordCount,
            preview.SampleScopedText,
            counts,
            preview.Diagnostics));
    }

    /// <summary>Previews URL rewrite rules and host mappings against a URL.</summary>
    /// <param name="request">Rewrite preview request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rewritten URL and the patterns that changed it.</returns>
    [HttpPost("rewrite-preview")]
    public IActionResult RewritePreview([FromBody] RewritePreviewRequest request, CancellationToken cancellationToken)
    {
        if (!IsAbsoluteUrl(request.Url))
        {
            return ValidationError("A valid absolute URL is required.", [new ApiFieldError("url", "Provide an http(s) URL.")]);
        }

        var applied = new List<string>();
        var current = request.Url;
        foreach (var rule in request.Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var locked = new UrlRewriteRule(rule.Pattern, rule.Replacement);
            var next = _rewriter.Rewrite(current, [locked]);
            if (!string.Equals(next, current, StringComparison.Ordinal))
            {
                applied.Add(rule.Pattern);
                current = next;
            }
        }

        current = ApplyHostMappings(current, request.HostMappings);
        return OkEnvelope(new RewritePreviewResponse(request.Url, current, applied));
    }

    /// <summary>Generates llms.txt / llms-full.txt into a local directory (E9).</summary>
    /// <param name="request">Generation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated variant, file name, and local path.</returns>
    [HttpPost("llms-generate")]
    public async Task<IActionResult> LlmsGenerate([FromBody] LlmsGenerateRequest request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.AuditId, out var auditId) || await _audits.GetByIdAsync(auditId, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{request.AuditId}' was not found.");
        }

        if (request.Variant is not ("llms-txt" or "llms-full-txt"))
        {
            return ValidationError($"Unknown variant '{request.Variant}'.", [new ApiFieldError("variant", "Use llms-txt or llms-full-txt.")]);
        }

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "exports", "llms");
        Directory.CreateDirectory(outputDirectory);
        var result = await _llmsGenerator.GenerateAsync(
            new LlmsGenerationRequest(auditId, outputDirectory, IncludeFullVersion: request.Variant == "llms-full-txt"),
            cancellationToken);

        var wanted = request.Variant == "llms-full-txt" ? "llms-full.txt" : "llms.txt";
        var path = result.WrittenFilePaths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), wanted, StringComparison.OrdinalIgnoreCase))
            ?? result.WrittenFilePaths.FirstOrDefault();
        if (path is null)
        {
            return InvalidStateError("The generator produced no file for the requested variant.");
        }

        return OkEnvelope(new LlmsGenerateResponse(request.Variant, Path.GetFileName(path), path));
    }

    /// <summary>Validates an llms.txt body or fetched URL against the convention.</summary>
    /// <param name="request">Validation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validity verdict and issue list.</returns>
    [HttpPost("llms-validate")]
    public async Task<IActionResult> LlmsValidate([FromBody] LlmsValidateRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Body) == !string.IsNullOrWhiteSpace(request.Url))
        {
            return ValidationError("Provide exactly one of body or url.", [new ApiFieldError("body", "body and url are mutually exclusive.")]);
        }

        var body = request.Body;
        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            var fetch = await _fetcher.FetchAsync(new HttpFetchRequest(request.Url), cancellationToken);
            body = Encoding.UTF8.GetString(fetch.Body.Span);
        }

        var validation = _geo.ValidateLlmsTxt(body!, request.Url);
        var issues = validation.Issues.Concat(validation.BrokenLinks.Select(l => $"Broken link: {l}")).ToList();
        return OkEnvelope(new LlmsValidateResponse(validation.IsPresent && validation.IsWellFormed, issues));
    }

    private static string ApplyHostMappings(string url, IReadOnlyList<HostMappingWire> mappings)
    {
        if (mappings.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        foreach (var mapping in mappings)
        {
            if (string.Equals(uri.Host, mapping.FromHost, StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri) { Host = mapping.ToHost };
                return builder.Uri.ToString();
            }
        }

        return url;
    }

    private static bool HasNoindexDirective(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "x-robots-tag", StringComparison.OrdinalIgnoreCase)
                && (header.Value.Contains("noindex", StringComparison.OrdinalIgnoreCase)
                    || header.Value.Contains("none", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAbsoluteUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string HttpStatusClass(int statusCode) => statusCode switch
    {
        >= 100 and < 200 => "1xx",
        >= 200 and < 300 => "2xx",
        >= 300 and < 400 => "3xx",
        >= 400 and < 500 => "4xx",
        _ => "5xx",
    };

    private static string LanguageWire(SelectorLanguage language) =>
        language == SelectorLanguage.XPath ? "xpath" : "css";
}
