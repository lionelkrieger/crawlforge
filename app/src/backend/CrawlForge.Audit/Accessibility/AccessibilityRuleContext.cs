using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility;

/// <summary>
/// One rule violation before finding materialization: rule key, element
/// locator/snippet, local message, and structured details.
/// </summary>
internal sealed record AccessibilityViolation(
    string RuleKey,
    string? Locator,
    string? XPath,
    string? ElementSnippet,
    string Message,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>
/// Structured accessibility evidence persisted with each finding
/// (FindingDraft.EvidenceJson) for the report surface and offline
/// re-reporting. Carries the element locator, remediation guidance, and the
/// bundled rule-set version that produced the finding.
/// </summary>
/// <param name="RuleKey">Stable rule key.</param>
/// <param name="RuleSetVersion">Bundled rule-set version.</param>
/// <param name="Family">Rule family.</param>
/// <param name="Locator">CSS-like element locator, when element-scoped.</param>
/// <param name="XPath">XPath element locator, when element-scoped.</param>
/// <param name="ElementSnippet">Bounded start-tag snippet, when element-scoped.</param>
/// <param name="Message">Local violation description.</param>
/// <param name="Remediation">Remediation guidance.</param>
/// <param name="RequiresRendering">True for render-dependent notices.</param>
/// <param name="Details">Structured rule-specific details.</param>
public sealed record AccessibilityEvidence(
    string RuleKey,
    string RuleSetVersion,
    string Family,
    string? Locator,
    string? XPath,
    string? ElementSnippet,
    string Message,
    string Remediation,
    bool RequiresRendering,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>
/// Shared evaluation state for one page: the effective document, render
/// availability flags, prebuilt indexes, and the violation sink.
/// </summary>
internal sealed class AccessibilityRuleContext
{
    /// <summary>Creates the context and builds document indexes.</summary>
    /// <param name="document">Effective document (rendered tree when available).</param>
    /// <param name="renderingEnabled">Configuration rendering mode is not None.</param>
    /// <param name="renderedTreeAvailable">A rendered document is present.</param>
    /// <param name="options">Effective local options.</param>
    public AccessibilityRuleContext(
        DomDocument document,
        bool renderingEnabled,
        bool renderedTreeAvailable,
        AccessibilityAnalyzerOptions options)
    {
        Document = document;
        RenderingEnabled = renderingEnabled;
        RenderedTreeAvailable = renderedTreeAvailable;
        Options = options;

        Elements = DomTraversal.Enumerate(document.Root);
        Nodes = Elements.Select(entry => entry.Node).ToList();
        foreach (var (node, _) in Elements)
        {
            var id = DomTraversal.Attr(node, "id");
            if (!string.IsNullOrWhiteSpace(id) && !ElementsById.ContainsKey(id))
            {
                ElementsById[id] = node;
            }
        }
    }

    /// <summary>The effective document under analysis.</summary>
    public DomDocument Document { get; }

    /// <summary>True when the configuration enables rendering.</summary>
    public bool RenderingEnabled { get; }

    /// <summary>True when the pipeline supplied a rendered tree.</summary>
    public bool RenderedTreeAvailable { get; }

    /// <summary>Effective local options.</summary>
    public AccessibilityAnalyzerOptions Options { get; }

    /// <summary>All elements in document order with locators.</summary>
    public IReadOnlyList<(DomNode Node, DomLocator Locator)> Elements { get; }

    /// <summary>All element nodes in document order (index-aligned with Elements).</summary>
    public IReadOnlyList<DomNode> Nodes { get; }

    /// <summary>First element per id (document order).</summary>
    public Dictionary<string, DomNode> ElementsById { get; } = new(StringComparer.Ordinal);

    /// <summary>Collected violations in evaluation order.</summary>
    public List<AccessibilityViolation> Violations { get; } = [];

    /// <summary>Records an element-scoped violation.</summary>
    public void Add(
        string ruleKey,
        DomNode element,
        DomLocator locator,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        Violations.Add(new AccessibilityViolation(
            ruleKey,
            locator.CssPath,
            locator.XPath,
            DomTraversal.StartTagSnippet(element),
            message,
            details));

    /// <summary>Records a document-scoped violation (no single element).</summary>
    public void AddDocument(string ruleKey, string message, IReadOnlyDictionary<string, string>? details = null) =>
        Violations.Add(new AccessibilityViolation(ruleKey, null, null, null, message, details));

    /// <summary>
    /// Records the render-dependent notice for a check that cannot run
    /// without the rendered tree (criterion: flagged when rendering is off).
    /// </summary>
    public void AddRequiresRenderingNotice(AccessibilityRuleDescriptor rule) =>
        Violations.Add(new AccessibilityViolation(
            rule.Key,
            null,
            null,
            null,
            $"Check '{rule.Key}' requires rendering (layout/computed style) and was not evaluated: "
                + (RenderingEnabled
                    ? "no rendered tree is available for this page."
                    : "rendering mode is disabled for this audit."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requiresRendering"] = "true",
                ["renderingEnabled"] = RenderingEnabled ? "true" : "false",
            }));

    /// <summary>Enumerates elements matching a tag name (case-insensitive).</summary>
    public IEnumerable<(DomNode Node, DomLocator Locator)> ByTag(string tagName)
    {
        foreach (var entry in Elements)
        {
            if (string.Equals(entry.Node.TagName, tagName, StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }

    /// <summary>Computes the simplified accessible name of an element.</summary>
    public string? AccessibleName(DomNode node) =>
        DomTraversal.AccessibleName(node, ElementsById, Nodes);
}
