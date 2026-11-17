using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// MarTech auditor (FS-CRAWL-002 §14.1/§14.3, locked
/// <see cref="IMarTechAuditor"/>): builds the tag/container inventory from
/// stored HTML/JS plus the proxy network capture, and raises Analytics &amp;
/// Tagging integrity findings — duplicate container injection, duplicate
/// initial-load pageview hits, missing primary container, and the no-hits
/// diagnostic. Offline re-analysis: operates only on stored artifacts, no
/// vendor lookups (E2). Enabled by default within bounds (criterion #47).
/// </summary>
public sealed class MarTechAuditor : IMarTechAuditor
{
    private const int MaxEvidenceHits = 5;

    private readonly MarTechOptions _options;
    private readonly string? _primaryContainer;

    /// <summary>Creates the auditor.</summary>
    /// <param name="options">Local options; null uses enabled defaults.</param>
    /// <param name="primaryContainer">
    /// Audit-wide primary container (from <see cref="PrimaryContainerResolver"/>
    /// aggregation); the configuration override wins when present.
    /// </param>
    public MarTechAuditor(MarTechOptions? options = null, string? primaryContainer = null)
    {
        _options = options ?? new MarTechOptions();
        _primaryContainer = primaryContainer;
    }

    /// <inheritdoc />
    public Task<MarTechAuditResult> AuditAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = _options.WithConfiguration(context.Configuration);
        if (!options.Enabled)
        {
            return Task.FromResult(new MarTechAuditResult([], []));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var document = context.RenderedDocument ?? context.Document;
        var detector = new TagInventoryDetector(options);
        var detections = detector.Detect(document, context.CapturedRequests, context.Url.CanonicalUrl);
        var findings = new List<FindingDraft>();
        var urlId = context.Url.Id;

        EvaluateDuplicateContainers(context, document, detections, findings, urlId);
        EvaluateDuplicatePageViews(context, findings, urlId);
        EvaluateNoHits(context, detections, findings, urlId);
        EvaluateMissingPrimary(context, options, detections, findings, urlId);

        return Task.FromResult(new MarTechAuditResult(
            detections.Select(detection => detection.ToDraft()).ToList(),
            findings));
    }

    /// <summary>
    /// Exposes the rich inventory for evidence/metric surfaces (additive;
    /// the locked interface returns drafts).
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <returns>Detailed detections.</returns>
    internal IReadOnlyList<TagDetection> DetectDetailed(PageAuditContext context)
    {
        var document = context.RenderedDocument ?? context.Document;
        return new TagInventoryDetector(_options).Detect(document, context.CapturedRequests, context.Url.CanonicalUrl);
    }

    private void EvaluateDuplicateContainers(
        PageAuditContext context,
        DomDocument? document,
        IReadOnlyList<TagDetection> detections,
        List<FindingDraft> findings,
        long urlId)
    {
        if (document is null)
        {
            return;
        }

        var scripts = EnumerateScripts(document.Root).ToList();
        foreach (var detection in detections.Where(d => d.FromDom && !string.IsNullOrEmpty(d.ContainerId)))
        {
            // Duplicate INJECTION counting: a container ID mentioned in a
            // loader src plus its config call is one injection, not two.
            // Count script loads carrying the ID, plus inline scripts that
            // embed a vendor load marker (container injection snippets).
            var signature = MarTechCatalogue.Find(detection.Vendor);
            var occurrences = 0;
            foreach (var script in scripts)
            {
                var src = Attr(script, "src");
                if (src is not null)
                {
                    if (src.Contains(detection.ContainerId!, StringComparison.Ordinal))
                    {
                        occurrences++;
                    }
                }
                else if (signature is not null
                    && script.TextContent.Contains(detection.ContainerId!, StringComparison.Ordinal)
                    && signature.ScriptSrcMarkers.Any(marker =>
                        script.TextContent.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    occurrences++;
                }
            }

            if (occurrences > 1)
            {
                findings.Add(MarTechFindingFactory.Create(
                    MarTechRuleKeys.DuplicateContainer,
                    FindingSeverity.Warning,
                    urlId,
                    new MarTechEvidence(
                        MarTechRuleKeys.DuplicateContainer,
                        detection.Vendor,
                        detection.ContainerId,
                        $"Container '{detection.ContainerId}' is injected {occurrences} times on this page.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["occurrences"] = occurrences.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["facet"] = "dom-injection",
                        })));
            }
        }
    }

    private static void EvaluateDuplicatePageViews(
        PageAuditContext context,
        List<FindingDraft> findings,
        long urlId)
    {
        if (context.CapturedRequests is not { } captures)
        {
            return;
        }

        var pageViews = captures
            .Where(request => NetworkHitClassifier.IsPageViewHit(request.Url))
            .GroupBy(request => NetworkHitClassifier.ContainerIdFromUrl(request.Url) ?? "(unknown)")
            .Where(group => group.Count() > 1);

        foreach (var group in pageViews)
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["facet"] = "network-pageview",
                ["count"] = group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            var index = 0;
            foreach (var request in group.Take(MaxEvidenceHits))
            {
                details[$"hit[{index}].url"] = request.Url;
                details[$"hit[{index}].capturedAt"] = request.CapturedAt.ToString("O");
                index++;
            }

            findings.Add(MarTechFindingFactory.Create(
                MarTechRuleKeys.DuplicateContainer,
                FindingSeverity.Warning,
                urlId,
                new MarTechEvidence(
                    MarTechRuleKeys.DuplicateContainer,
                    Vendor: null,
                    group.Key == "(unknown)" ? null : group.Key,
                    $"Duplicate page_view hits observed on initial load for '{group.Key}' ({group.Count()} requests).",
                    details)));
        }
    }

    private static void EvaluateNoHits(
        PageAuditContext context,
        IReadOnlyList<TagDetection> detections,
        List<FindingDraft> findings,
        long urlId)
    {
        if (context.CapturedRequests is not { } captures)
        {
            return; // Capture not enabled: the diagnostic does not apply.
        }

        foreach (var detection in detections.Where(d =>
            d.FromDom && !d.FromNetwork
            && d.Kind is MarTechCatalogue.KindContainer or MarTechCatalogue.KindAnalytics))
        {
            var signature = MarTechCatalogue.Find(detection.Vendor);
            var vendorHits = signature is null
                ? 0
                : captures.Count(request => signature.NetworkMarkers.Any(marker =>
                    request.Url.Contains(marker, StringComparison.OrdinalIgnoreCase)));
            if (vendorHits > 0)
            {
                continue;
            }

            findings.Add(MarTechFindingFactory.Create(
                MarTechRuleKeys.NoHitsObserved,
                FindingSeverity.Notice,
                urlId,
                new MarTechEvidence(
                    MarTechRuleKeys.NoHitsObserved,
                    detection.Vendor,
                    detection.ContainerId,
                    $"Container '{detection.ContainerId ?? detection.Vendor}' is present in the DOM but zero "
                        + "corresponding network hits were observed while capture was enabled.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["capturedRequests"] = captures.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    })));
        }
    }

    private void EvaluateMissingPrimary(
        PageAuditContext context,
        MarTechOptions options,
        IReadOnlyList<TagDetection> detections,
        List<FindingDraft> findings,
        long urlId)
    {
        var primary = options.PrimaryContainerOverride ?? _primaryContainer;
        if (string.IsNullOrWhiteSpace(primary) || !context.Url.IsIndexable)
        {
            return;
        }

        if (detections.Any(detection =>
            string.Equals(detection.ContainerId, primary, StringComparison.Ordinal)))
        {
            return;
        }

        findings.Add(MarTechFindingFactory.Create(
            MarTechRuleKeys.MissingContainer,
            FindingSeverity.Warning,
            urlId,
            new MarTechEvidence(
                MarTechRuleKeys.MissingContainer,
                Vendor: null,
                primary,
                $"Indexable page is missing the site's primary container '{primary}'.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["primarySource"] = options.PrimaryContainerOverride is not null ? "user-override" : "audit-aggregate",
                })));
    }

    private static IEnumerable<DomNode> EnumerateScripts(DomNode node)
    {
        if (node.TagName == "script")
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var script in EnumerateScripts(child))
            {
                yield return script;
            }
        }
    }

    private static string? Attr(DomNode node, string name) =>
        node.Attributes.TryGetValue(name, out var value) ? value : null;
}
