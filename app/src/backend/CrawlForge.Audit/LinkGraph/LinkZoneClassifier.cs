using System.Runtime.CompilerServices;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.LinkGraph;

/// <summary>
/// Local options for <see cref="LinkZoneClassifier"/>. Content-region selectors
/// implement the Content Area Scoping interaction of FS-CRAWL-002 §15.1: anchors
/// inside a scoped content region classify as in-content. Supported selector
/// forms: tag name ("main"), "#id", ".class", "[role=value]".
/// </summary>
public sealed record LinkZoneClassifierOptions
{
    /// <summary>Content-region selectors; defaults to main/article/role=main landmarks.</summary>
    public IReadOnlyList<string> ContentRegionSelectors { get; init; } = ["main", "article", "[role=main]"];
}

/// <summary>
/// DOM-zone link classifier (FS-CRAWL-002 §15.1). Deterministic precedence:
/// breadcrumb marker &gt; scoped content region (in-content) &gt; nav/role=navigation
/// &gt; header/role=banner &gt; footer/role=contentinfo &gt; aside/role=complementary
/// (sidebar) &gt; default in-content for body anchors without boilerplate landmarks.
/// Zone precedence applies across the whole ancestor chain (a nav inside a footer
/// classifies as navigation). Every classification carries human-readable evidence
/// (marker tag/attribute and ancestor depth) for the additive zone_evidence column.
/// Anchors outside the document body classify as the residual Other zone.
/// </summary>
public sealed class LinkZoneClassifier : ILinkZoneClassifier
{
    private static readonly ConditionalWeakTable<DomDocument, ParentIndex> IndexCache = new();

    private readonly LinkZoneClassifierOptions _options;

    /// <summary>Creates the classifier.</summary>
    /// <param name="options">Local options; null uses the default content-region landmarks.</param>
    public LinkZoneClassifier(LinkZoneClassifierOptions? options = null)
    {
        _options = options ?? new LinkZoneClassifierOptions();
    }

    /// <inheritdoc />
    public LinkZoneResult Classify(DomDocument document, DomNode anchorNode)
    {
        var index = IndexCache.GetValue(document, static d => ParentIndex.Build(d.Root));

        // Collect the ancestor chain (nearest first), including the anchor itself.
        var chain = new List<DomNode> { anchorNode };
        var current = anchorNode;
        while (index.TryGetParent(current, out var parent))
        {
            chain.Add(parent);
            current = parent;
        }

        var attached = chain.Count > 1 || ReferenceEquals(anchorNode, document.Root);

        // 1. Breadcrumb markers win everywhere in the chain.
        for (var depth = 0; depth < chain.Count; depth++)
        {
            if (TryDescribeBreadcrumbMarker(chain[depth], out var marker))
            {
                return new LinkZoneResult(DomZone.Breadcrumb, $"breadcrumb marker {marker} at depth {depth}");
            }
        }

        // 2. Scoped content region → in-content (§15.1 Content Area Scoping clause).
        for (var depth = 0; depth < chain.Count; depth++)
        {
            if (MatchesContentRegion(chain[depth]))
            {
                return new LinkZoneResult(DomZone.MainContent, $"content region <{chain[depth].TagName}> at depth {depth}");
            }
        }

        // 3. Boilerplate landmarks by fixed zone precedence across the chain.
        if (TryFindLandmark(chain, "nav", "navigation", out var navDepth))
        {
            return new LinkZoneResult(DomZone.Navigation, $"landmark nav/role=navigation at depth {navDepth}");
        }

        if (TryFindLandmark(chain, "header", "banner", out var headerDepth))
        {
            return new LinkZoneResult(DomZone.Header, $"landmark header/role=banner at depth {headerDepth}");
        }

        if (TryFindLandmark(chain, "footer", "contentinfo", out var footerDepth))
        {
            return new LinkZoneResult(DomZone.Footer, $"landmark footer/role=contentinfo at depth {footerDepth}");
        }

        if (TryFindLandmark(chain, "aside", "complementary", out var asideDepth))
        {
            return new LinkZoneResult(DomZone.Sidebar, $"landmark aside/role=complementary at depth {asideDepth}");
        }

        // 4. Residual: anchors outside the body when a body exists.
        if (attached && index.HasBody && !chain.Any(n => n.TagName == "body"))
        {
            return new LinkZoneResult(DomZone.Other, "anchor outside document body");
        }

        // 5. Default in-content for body anchors without boilerplate landmarks.
        return new LinkZoneResult(
            DomZone.MainContent,
            attached ? "default: body anchor without boilerplate landmark" : "default: detached anchor without boilerplate landmark");
    }

    private static bool TryFindLandmark(List<DomNode> chain, string tag, string role, out int depth)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (node.TagName == tag || GetAttribute(node, "role") == role)
            {
                depth = i;
                return true;
            }
        }

        depth = -1;
        return false;
    }

    private static bool TryDescribeBreadcrumbMarker(DomNode node, out string marker)
    {
        var ariaLabel = GetAttribute(node, "aria-label");
        if (ariaLabel is not null && ariaLabel.Contains("breadcrumb", StringComparison.OrdinalIgnoreCase))
        {
            marker = $"aria-label='{ariaLabel}' on <{node.TagName}>";
            return true;
        }

        var itemtype = GetAttribute(node, "itemtype");
        if (itemtype is not null && itemtype.Contains("BreadcrumbList", StringComparison.OrdinalIgnoreCase))
        {
            marker = $"itemtype='{itemtype}' on <{node.TagName}>";
            return true;
        }

        var classes = GetAttribute(node, "class");
        if (classes is not null && classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Contains("breadcrumb", StringComparison.OrdinalIgnoreCase)))
        {
            marker = $"class='{classes}' on <{node.TagName}>";
            return true;
        }

        var id = GetAttribute(node, "id");
        if (id is not null && id.Contains("breadcrumb", StringComparison.OrdinalIgnoreCase))
        {
            marker = $"id='{id}' on <{node.TagName}>";
            return true;
        }

        marker = string.Empty;
        return false;
    }

    private bool MatchesContentRegion(DomNode node)
    {
        foreach (var selector in _options.ContentRegionSelectors)
        {
            if (MatchesSelector(node, selector))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSelector(DomNode node, string selector)
    {
        if (selector.StartsWith('#'))
        {
            return GetAttribute(node, "id") == selector[1..];
        }

        if (selector.StartsWith('.'))
        {
            var classes = GetAttribute(node, "class");
            return classes is not null && classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(selector[1..], StringComparer.Ordinal);
        }

        if (selector.StartsWith('[') && selector.EndsWith(']'))
        {
            var inner = selector[1..^1];
            var separator = inner.IndexOf('=');
            if (separator > 0)
            {
                var name = inner[..separator];
                var value = inner[(separator + 1)..].Trim('"', '\'');
                return GetAttribute(node, name) == value;
            }

            return node.Attributes.ContainsKey(inner);
        }

        return node.TagName == selector;
    }

    private static string? GetAttribute(DomNode node, string name) =>
        node.Attributes.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Reference-keyed parent index over the contract DOM tree (records use value
    /// equality, so reference identity is mandatory). Built iteratively — safe for
    /// deeply nested documents — and cached per document instance.
    /// </summary>
    private sealed class ParentIndex
    {
        private readonly Dictionary<DomNode, DomNode> _parents = new(ReferenceEqualityComparer.Instance);

        public bool HasBody { get; private set; }

        public static ParentIndex Build(DomNode root)
        {
            var index = new ParentIndex();
            var stack = new Stack<DomNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.TagName == "body")
                {
                    index.HasBody = true;
                }

                foreach (var child in node.Children)
                {
                    index._parents[child] = node;
                    stack.Push(child);
                }
            }

            return index;
        }

        public bool TryGetParent(DomNode node, out DomNode parent) =>
            _parents.TryGetValue(node, out parent!);
    }
}
