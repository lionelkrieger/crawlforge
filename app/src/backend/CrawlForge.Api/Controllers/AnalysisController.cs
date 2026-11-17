using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Standalone single-page analysis (FS-CRAWL-001 §7.7, REST:
/// POST /api/analysis/single-page): findings and element inventory for one URL
/// without a full audit, plus the additive WAF verdict and scope diagnostics.
/// </summary>
[Route("api/analysis")]
public sealed class AnalysisController : ApiControllerBase
{
    private readonly ISinglePageAnalyzer _analyzer;

    /// <summary>Creates the controller.</summary>
    /// <param name="analyzer">Single-page analyzer.</param>
    public AnalysisController(ISinglePageAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    /// <summary>Analyzes a single page on demand.</summary>
    /// <param name="request">Single-page analysis request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Findings, inventory, and additive diagnostics.</returns>
    [HttpPost("single-page")]
    public async Task<IActionResult> SinglePage([FromBody] SinglePageAnalysisRequestWire request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ValidationError("A valid absolute URL is required.", [new Contracts.Common.ApiFieldError("url", "Provide an http(s) URL.")]);
        }

        var result = await _analyzer.AnalyzeAsync(
            new SinglePageAnalysisRequest(request.Url, request.Render ?? false, request.RuleKeys),
            cancellationToken);

        var findings = result.Findings.Select(AuditWireMapper.ToFindingDraft).ToList();
        var counts = result.Inventory
            .GroupBy(i => i.Group, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var inventory = new ElementInventoryWire(counts, result.Inventory.Count);

        return OkEnvelope(new SinglePageAnalysisResponse(
            findings,
            inventory,
            result.WafVerdict is { IsChallenge: true } verdict ? AuditWireMapper.ToWafVerdict(verdict) : null,
            ProjectScope(result.ScopeDiagnostics)));
    }

    /// <summary>
    /// Projects the resolved scoped text onto the wire diagnostics. The locked
    /// <see cref="ScopedVisibleText"/> exposes the scoped word count and resolver
    /// diagnostics only; matched-selector lists and the pre-scope total are not
    /// surfaced by the analyzer, so they project as empty/equal (handoff note).
    /// </summary>
    private static ScopeDiagnosticsWire? ProjectScope(ScopedVisibleText? scoped) =>
        scoped is null
            ? null
            : new ScopeDiagnosticsWire(true, [], [], scoped.WordCount, scoped.WordCount);
}
