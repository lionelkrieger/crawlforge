using System.Text.Json;

namespace CrawlForge.Api.Controllers;

/// <summary>POST /api/tools/robots-test request: robots body OR URL, test URLs, user-agent.</summary>
/// <param name="RobotsBody">Robots.txt body (mutually exclusive with <paramref name="RobotsUrl"/>).</param>
/// <param name="RobotsUrl">Robots.txt URL to fetch from the crawl target.</param>
/// <param name="TestUrls">URLs to evaluate against the robots document.</param>
/// <param name="UserAgent">User-agent token to match.</param>
public sealed record RobotsTestRequest(string? RobotsBody, string? RobotsUrl, IReadOnlyList<string> TestUrls, string UserAgent);

/// <summary>Allow/disallow verdict and matched rule for one test URL.</summary>
/// <param name="Url">Tested URL.</param>
/// <param name="Allowed">Whether crawling is allowed.</param>
/// <param name="MatchedRule">The deciding rule pattern, when any.</param>
public sealed record RobotsTestResultWire(string Url, bool Allowed, string? MatchedRule);

/// <summary>Per-URL robots verdicts.</summary>
/// <param name="Results">Verdicts in request order.</param>
public sealed record RobotsTestResponse(IReadOnlyList<RobotsTestResultWire> Results);

/// <summary>POST /api/tools/url-check request.</summary>
/// <param name="Url">Target URL.</param>
public sealed record UrlCheckRequest(string Url);

/// <summary>URL-check verdict: status, chain, headers, indexability, additive WAF/scope.</summary>
/// <param name="StatusCode">Observed HTTP status code.</param>
/// <param name="StatusClass">HTTP status class wire key.</param>
/// <param name="RedirectChain">Observed redirect hops.</param>
/// <param name="Headers">Final response headers.</param>
/// <param name="Indexable">Whether the page is indexable.</param>
/// <param name="WafVerdict">Additive WAF/challenge verdict, when detected.</param>
/// <param name="ScopeDiagnostics">Additive scope diagnostics, when scoping ran.</param>
public sealed record UrlCheckResponse(
    int StatusCode,
    string StatusClass,
    IReadOnlyList<RedirectHopWire> RedirectChain,
    IReadOnlyDictionary<string, string> Headers,
    bool Indexable,
    WafDetectionWire? WafVerdict,
    ScopeDiagnosticsWire? ScopeDiagnostics);

/// <summary>POST /api/tools/structured-data-check request: markup OR URL.</summary>
/// <param name="Markup">HTML/markup fragment containing structured data.</param>
/// <param name="Url">URL whose document is checked.</param>
public sealed record StructuredDataCheckRequest(string? Markup, string? Url);

/// <summary>One parsed structured-data block with validation/rich-result outcome.</summary>
/// <param name="Format">Markup format wire key (json-ld, microdata, rdfa).</param>
/// <param name="Type">Primary schema type, when identified.</param>
/// <param name="Properties">Parsed properties (empty when the validator exposes none).</param>
/// <param name="Valid">Whether the block validates.</param>
/// <param name="RichResultEligible">Whether the block is rich-result eligible.</param>
/// <param name="Issues">Validation issues.</param>
public sealed record StructuredDataBlockWire(
    string Format,
    string Type,
    IReadOnlyDictionary<string, JsonElement> Properties,
    bool Valid,
    bool RichResultEligible,
    IReadOnlyList<string> Issues);

/// <summary>Parsed structured-data blocks.</summary>
/// <param name="Blocks">Blocks in document order.</param>
public sealed record StructuredDataCheckResponse(IReadOnlyList<StructuredDataBlockWire> Blocks);

/// <summary>POST /api/tools/render-test request.</summary>
/// <param name="Url">Target URL.</param>
/// <param name="DeviceProfile">Device profile name; defaults to desktop.</param>
/// <param name="Picker">Additive: enable the CSS/XPath selector picker.</param>
public sealed record RenderTestRequest(string Url, string? DeviceProfile, bool? Picker);

/// <summary>One captured browser console message.</summary>
/// <param name="Level">Console level wire key.</param>
/// <param name="Text">Message text.</param>
/// <param name="Timestamp">Capture timestamp (ISO-8601), when present.</param>
public sealed record ConsoleMessageWire(string Level, string Text, string? Timestamp);

/// <summary>Render-test result: screenshot, rendered DOM, console, additive WAF/picker.</summary>
/// <param name="ScreenshotBase64">Base64-encoded screenshot bytes.</param>
/// <param name="RenderedHtml">Rendered DOM HTML.</param>
/// <param name="ConsoleMessages">Captured console messages.</param>
/// <param name="FinalUrl">Final URL after navigation.</param>
/// <param name="PostRenderWafVerdict">Additive post-render WAF verdict, when detected.</param>
/// <param name="PickerEnabled">Whether the selector picker is enabled.</param>
public sealed record RenderTestResponse(
    string ScreenshotBase64,
    string RenderedHtml,
    IReadOnlyList<ConsoleMessageWire> ConsoleMessages,
    string? FinalUrl,
    WafDetectionWire? PostRenderWafVerdict,
    bool PickerEnabled);

/// <summary>POST /api/tools/sitemap-generate request.</summary>
/// <param name="AuditId">Audit whose eligible URLs feed the sitemap.</param>
/// <param name="IncludeIndex">Advisory: prefer a sitemap index (engine splits above its bound).</param>
public sealed record SitemapGenerateRequest(string AuditId, bool? IncludeIndex);

/// <summary>Sitemap generation result (local file only).</summary>
/// <param name="FileName">Generated file name.</param>
/// <param name="UrlCount">Eligible URL count.</param>
public sealed record SitemapGenerateResponse(string FileName, long UrlCount);

/// <summary>POST /api/tools/scope-preview request.</summary>
/// <param name="SampleHtml">Sample document to preview against.</param>
/// <param name="ContentScope">Scope to preview; null previews the baseline.</param>
public sealed record ScopePreviewRequest(string SampleHtml, ContentScopeSettingsWire? ContentScope);

/// <summary>One matched include region in a scope preview.</summary>
/// <param name="Selector">Selector that matched the region.</param>
/// <param name="Language">Selector language wire key (css, xpath).</param>
/// <param name="TagName">Root tag of the matched region.</param>
/// <param name="TextSample">Bounded visible-text sample.</param>
/// <param name="WordCount">Visible word count of the region.</param>
public sealed record MatchedRegionWire(string Selector, string Language, string TagName, string TextSample, int WordCount);

/// <summary>Per-selector match count in a scope preview.</summary>
/// <param name="Expression">Selector expression.</param>
/// <param name="Language">Selector language wire key.</param>
/// <param name="MatchCount">Number of matched nodes.</param>
public sealed record SelectorMatchCountWire(string Expression, string Language, int MatchCount);

/// <summary>Scope-preview result.</summary>
/// <param name="MatchedRegions">Matched include regions in document order.</param>
/// <param name="FullWordCount">Visible word count before scoping.</param>
/// <param name="ScopedWordCount">Visible word count after scoping.</param>
/// <param name="RemovedWordCount">Words removed by scoping.</param>
/// <param name="SampleScopedText">Bounded sample of the scoped text.</param>
/// <param name="PerSelectorCounts">Per-entry match counts.</param>
/// <param name="Diagnostics">Resolver diagnostics.</param>
public sealed record ScopePreviewResponse(
    IReadOnlyList<MatchedRegionWire> MatchedRegions,
    int FullWordCount,
    int ScopedWordCount,
    int RemovedWordCount,
    string SampleScopedText,
    IReadOnlyList<SelectorMatchCountWire> PerSelectorCounts,
    IReadOnlyList<string> Diagnostics);

/// <summary>POST /api/tools/rewrite-preview request.</summary>
/// <param name="Url">Input URL.</param>
/// <param name="Rules">Rewrite rules in application order.</param>
/// <param name="HostMappings">Host equivalence mappings applied after rewriting.</param>
public sealed record RewritePreviewRequest(string Url, IReadOnlyList<UrlRewriteRuleWire> Rules, IReadOnlyList<HostMappingWire> HostMappings);

/// <summary>Rewrite-preview result.</summary>
/// <param name="OriginalUrl">Input URL.</param>
/// <param name="RewrittenUrl">URL after rules and host mappings.</param>
/// <param name="AppliedRules">Patterns of the rules that changed the URL.</param>
public sealed record RewritePreviewResponse(string OriginalUrl, string RewrittenUrl, IReadOnlyList<string> AppliedRules);

/// <summary>POST /api/tools/llms-generate request (local export only, E9).</summary>
/// <param name="AuditId">Audit whose content feeds generation.</param>
/// <param name="Variant">llms-txt or llms-full-txt.</param>
/// <param name="Hosts">Optional host restriction.</param>
public sealed record LlmsGenerateRequest(string AuditId, string Variant, IReadOnlyList<string>? Hosts);

/// <summary>llms.txt generation result.</summary>
/// <param name="Variant">Generated variant wire key.</param>
/// <param name="FileName">Generated file name.</param>
/// <param name="FilePath">Local file path written.</param>
public sealed record LlmsGenerateResponse(string Variant, string FileName, string FilePath);

/// <summary>POST /api/tools/llms-validate request: body OR URL.</summary>
/// <param name="Body">llms.txt content.</param>
/// <param name="Url">llms.txt URL to fetch and validate.</param>
public sealed record LlmsValidateRequest(string? Body, string? Url);

/// <summary>llms.txt validation outcome.</summary>
/// <param name="Valid">Whether the document follows the convention.</param>
/// <param name="Issues">Validation issues.</param>
public sealed record LlmsValidateResponse(bool Valid, IReadOnlyList<string> Issues);

/// <summary>POST /api/analysis/single-page request.</summary>
/// <param name="Url">Target URL.</param>
/// <param name="Render">Whether to render before analysis.</param>
/// <param name="RuleKeys">Optional rule filter.</param>
public sealed record SinglePageAnalysisRequestWire(string Url, bool? Render, IReadOnlyList<string>? RuleKeys);

/// <summary>Single-page analysis result.</summary>
/// <param name="Findings">Findings for the page.</param>
/// <param name="ElementInventory">Element inventory counts.</param>
/// <param name="WafVerdict">Additive WAF verdict, when evaluated.</param>
/// <param name="ScopeDiagnostics">Additive scope diagnostics, when scoping ran.</param>
public sealed record SinglePageAnalysisResponse(
    IReadOnlyList<FindingWire> Findings,
    ElementInventoryWire ElementInventory,
    WafDetectionWire? WafVerdict,
    ScopeDiagnosticsWire? ScopeDiagnostics);
