namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Stable rule keys emitted by the indexability analyzer family
/// (FS-CRAWL-001 §7.6, §7.12; FS-CRAWL-002 §15.4). The three header/HTML
/// conflict keys are the locked <c>Contracts.Constants.FindingRuleKeys</c>
/// values; the base indexability keys below are additive stable strings in the
/// open rule-key taxonomy (schema catalog: findings.rule key). Keys are never
/// repurposed; new keys arrive additively.
/// </summary>
public static class IndexabilityRuleKeys
{
    /// <summary>A noindex directive was observed (meta robots or X-Robots-Tag).</summary>
    public const string Noindex = "indexability.noindex";

    /// <summary>A nofollow directive was observed (meta robots or X-Robots-Tag).</summary>
    public const string Nofollow = "indexability.nofollow";

    /// <summary>Robots exclusion blocks crawling of the URL.</summary>
    public const string RobotsBlocked = "indexability.robots-blocked";

    /// <summary>The final status class is not indexable (non-2xx or blocked).</summary>
    public const string NonIndexableStatus = "indexability.non-indexable-status";

    /// <summary>A success-status page matches error-like content (soft 404).</summary>
    public const string Soft404 = "indexability.soft-404";

    /// <summary>The page is indexable (Pass annotation).</summary>
    public const string Indexable = "indexability.indexable";

    /// <summary>No canonical directive is present on an indexable page.</summary>
    public const string CanonicalMissing = "canonical.missing";

    /// <summary>The canonical target differs from the page's canonical URL.</summary>
    public const string CanonicalMismatch = "canonical.mismatch";

    /// <summary>The canonical target points to a different host.</summary>
    public const string CanonicalCrossHost = "canonical.cross-host";

    /// <summary>More than one canonical link element is present.</summary>
    public const string CanonicalMultiple = "canonical.multiple";

    /// <summary>The canonical target is malformed or non-http(s).</summary>
    public const string CanonicalInvalid = "canonical.invalid";

    /// <summary>The canonical self-reference check passed (Pass annotation).</summary>
    public const string CanonicalSelfReference = "canonical.self-reference";

    /// <summary>Content differs between raw response and rendered document.</summary>
    public const string RenderTextDelta = "render.text-delta";

    /// <summary>The title differs between raw response and rendered document.</summary>
    public const string RenderTitleDelta = "render.title-delta";

    /// <summary>Metadata was altered by script between response and render.</summary>
    public const string RenderMetadataDelta = "render.metadata-delta";

    /// <summary>Links are present only after rendering.</summary>
    public const string RenderLinksDelta = "render.links-delta";

    /// <summary>Structured data differs between response and render.</summary>
    public const string RenderStructuredDataDelta = "render.structured-data-delta";

    /// <summary>Resource sets differ between response and render.</summary>
    public const string RenderResourcesDelta = "render.resources-delta";

    /// <summary>Status differs between the raw fetch and the final rendered outcome.</summary>
    public const string RenderStatusDelta = "render.status-delta";
}