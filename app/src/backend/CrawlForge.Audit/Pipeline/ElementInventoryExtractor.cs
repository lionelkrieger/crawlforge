using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;

namespace CrawlForge.Audit.Pipeline;

/// <summary>
/// Extracts the DOM element inventory (FS-CRAWL-001 §7.3) as url_elements
/// group instances: document head, headings outline, anchors, images,
/// structured data, resources, text statistics, tables, and link relations —
/// plus the document-derived additive groups llms_structure, consent_state,
/// datalayer_snapshot, and serp_metrics (FS-CRAWL-002 Appendix A). The
/// context-derived groups content_scope and header_directives are appended by
/// the pipeline, which holds the resolved scope and fetch headers. Scoping
/// never alters head facts or link discovery (§4.3 non-consumers); the
/// text_statistics group reports the full visible word count while scoped
/// counts ride the content_scope group.
/// </summary>
public sealed class ElementInventoryExtractor : IElementInventoryExtractor
{
    private const int StructuredDataBound = 8192;
    private const int DataLayerSnippetBound = 1024;
    private const int DataLayerMaxSnippets = 10;
    private const int ConsentMarkerBound = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string[] ConsentPlatformHints =
    [
        "onetrust", "cookiebot", "trustarc", "quantcast", "didomi", "usercentrics", "osano",
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<ElementInventoryItem>> ExtractAsync(
        DomDocument document,
        CancellationToken cancellationToken = default)
    {
        var items = new List<ElementInventoryItem>();
        var elements = Flatten(document.Root);

        items.Add(Item("document_head", 0, ExtractHead(elements)));
        items.Add(Item("headings", 0, ExtractHeadings(elements)));

        var ordinal = 0;
        foreach (var anchor in elements.Where(e => e.TagName == "a"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(Item("anchors", ordinal++, new
            {
                href = Attr(anchor, "href"),
                text = VisibleTextOf(anchor),
                rel = Attr(anchor, "rel"),
                target = Attr(anchor, "target"),
                position = ordinal - 1,
            }));
        }

        ordinal = 0;
        foreach (var image in elements.Where(e => e.TagName == "img"))
        {
            items.Add(Item("images", ordinal++, new
            {
                src = Attr(image, "src"),
                srcset = Attr(image, "srcset"),
                alt = Attr(image, "alt"),
                width = Attr(image, "width"),
                height = Attr(image, "height"),
                loading = Attr(image, "loading"),
            }));
        }

        ordinal = 0;
        foreach (var block in ExtractStructuredData(elements))
        {
            items.Add(Item("structured_data", ordinal++, block));
        }

        items.Add(Item("resources", 0, ExtractResources(elements)));
        items.Add(Item("text_statistics", 0, new
        {
            visibleWordCount = CountWords(VisibleTextOf(document.Root)),
            declaredLanguage = Attr(document.Root, "lang"),
        }));

        ordinal = 0;
        foreach (var table in elements.Where(e => e.TagName == "table"))
        {
            items.Add(Item("tables", ordinal++, ExtractTable(table)));
        }

        items.Add(Item("link_relations", 0, ExtractLinkRelations(elements)));

        if (IsLlmsTxt(document.Url))
        {
            items.Add(Item(ElementGroups.LlmsStructure, 0, ExtractLlmsStructure(document.Root)));
        }

        items.Add(Item(ElementGroups.ConsentState, 0, ExtractConsentState(elements)));
        items.Add(Item(ElementGroups.DataLayerSnapshot, 0, ExtractDataLayer(elements)));
        items.Add(Item(ElementGroups.SerpMetrics, 0, ExtractSerpMetrics(elements)));

        return Task.FromResult<IReadOnlyList<ElementInventoryItem>>(items);
    }

    private static ElementInventoryItem Item(string group, int ordinal, object payload) =>
        new(group, JsonSerializer.Serialize(payload, JsonOptions), ordinal);

    private static List<DomNode> Flatten(DomNode root)
    {
        var result = new List<DomNode>();
        var stack = new Stack<DomNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            result.Add(node);
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return result;
    }

    private static string? Attr(DomNode node, string name) =>
        node.Attributes.TryGetValue(name, out var value) ? value : null;

    private static string VisibleTextOf(DomNode node)
    {
        var builder = new StringBuilder();
        AppendVisible(node, builder);
        return Normalize(builder.ToString());
    }

    private static void AppendVisible(DomNode node, StringBuilder builder)
    {
        if (node.TagName is "script" or "style" or "noscript" or "template" or "head")
        {
            return;
        }

        if (node.Attributes.ContainsKey("hidden"))
        {
            return;
        }

        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            AppendVisible(child, builder);
        }
    }

    private static void AppendRawVisible(DomNode node, StringBuilder builder)
    {
        if (node.TagName is "script" or "style" or "noscript" or "template" or "head")
        {
            return;
        }

        if (node.Attributes.ContainsKey("hidden"))
        {
            return;
        }

        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            AppendRawVisible(child, builder);
        }
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static int CountWords(string text) =>
        text.Length == 0 ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static object ExtractHead(List<DomNode> elements)
    {
        var head = elements.FirstOrDefault(e => e.TagName == "head");
        var headNodes = head is null ? [] : Flatten(head);

        string? MetaContent(string name) => headNodes
            .Where(e => e.TagName == "meta" &&
                string.Equals(Attr(e, "name"), name, StringComparison.OrdinalIgnoreCase))
            .Select(e => Attr(e, "content"))
            .FirstOrDefault();

        var title = headNodes.FirstOrDefault(e => e.TagName == "title");
        var canonical = headNodes
            .Where(e => e.TagName == "link" && Attr(e, "rel")?.Contains("canonical", StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => Attr(e, "href"))
            .FirstOrDefault();

        var alternates = headNodes
            .Where(e => e.TagName == "link" && Attr(e, "rel")?.Contains("alternate", StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new { hreflang = Attr(e, "hreflang"), href = Attr(e, "href"), type = Attr(e, "type") })
            .ToArray();

        var openGraph = headNodes
            .Where(e => e.TagName == "meta" && Attr(e, "property")?.StartsWith("og:", StringComparison.OrdinalIgnoreCase) == true)
            .ToDictionary(e => Attr(e, "property")!, e => Attr(e, "content") ?? string.Empty);

        var twitter = headNodes
            .Where(e => e.TagName == "meta" && Attr(e, "name")?.StartsWith("twitter:", StringComparison.OrdinalIgnoreCase) == true)
            .ToDictionary(e => Attr(e, "name")!, e => Attr(e, "content") ?? string.Empty);

        var favicons = headNodes
            .Where(e => e.TagName == "link" && Attr(e, "rel")?.Contains("icon", StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new { rel = Attr(e, "rel"), href = Attr(e, "href"), sizes = Attr(e, "sizes") })
            .ToArray();

        var charset = headNodes
            .Where(e => e.TagName == "meta")
            .Select(e => Attr(e, "charset") ?? MetaContent("content-type"))
            .FirstOrDefault(c => c is not null);

        return new
        {
            title = title is null ? null : VisibleTextOf(title),
            metaDescription = MetaContent("description"),
            metaRobots = MetaContent("robots"),
            viewport = MetaContent("viewport"),
            charset,
            canonicalUrl = canonical,
            alternates,
            openGraph,
            twitterCard = twitter,
            favicons,
        };
    }

    private static object ExtractHeadings(List<DomNode> elements) => new
    {
        outline = elements
            .Where(e => e.TagName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .Select(e => new { level = int.Parse(e.TagName[1..]), text = VisibleTextOf(e) })
            .ToArray(),
    };

    private static IEnumerable<object> ExtractStructuredData(List<DomNode> elements)
    {
        foreach (var script in elements.Where(e =>
            e.TagName == "script" &&
            string.Equals(Attr(e, "type"), "application/ld+json", StringComparison.OrdinalIgnoreCase)))
        {
            var raw = string.Concat(script.Children.Select(c => c.TextContent)) + script.TextContent;
            yield return new
            {
                type = "json-ld",
                raw = raw.Length <= StructuredDataBound ? raw : raw[..StructuredDataBound],
                truncated = raw.Length > StructuredDataBound,
            };
        }

        foreach (var scope in elements.Where(e => e.Attributes.ContainsKey("itemscope")))
        {
            var properties = Flatten(scope)
                .Where(e => e.Attributes.ContainsKey("itemprop"))
                .Select(e => new { name = Attr(e, "itemprop"), value = VisibleTextOf(e) })
                .Take(50)
                .ToArray();
            yield return new
            {
                type = "microdata",
                itemType = Attr(scope, "itemtype"),
                properties,
            };
        }
    }

    private static object ExtractResources(List<DomNode> elements)
    {
        var head = elements.FirstOrDefault(e => e.TagName == "head");
        var headIds = head is null ? new HashSet<DomNode>() : new HashSet<DomNode>(Flatten(head));

        var scripts = elements
            .Where(e => e.TagName == "script")
            .Select(e => new
            {
                src = Attr(e, "src"),
                placement = headIds.Contains(e) ? "head" : "body",
                async = e.Attributes.ContainsKey("async"),
                defer = e.Attributes.ContainsKey("defer"),
                type = Attr(e, "type"),
            })
            .ToArray();

        var stylesheets = elements
            .Where(e => e.TagName == "link" && Attr(e, "rel")?.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new { href = Attr(e, "href"), media = Attr(e, "media") })
            .ToArray();

        var inlineScriptBytes = elements
            .Where(e => e.TagName == "script" && Attr(e, "src") is null)
            .Sum(e => Encoding.UTF8.GetByteCount(e.TextContent));
        var inlineStyleBytes = elements
            .Where(e => e.TagName == "style")
            .Sum(e => Encoding.UTF8.GetByteCount(e.TextContent));

        return new
        {
            scripts,
            stylesheets,
            scriptCount = scripts.Length,
            stylesheetCount = stylesheets.Length,
            inlineScriptBytes,
            inlineStyleBytes,
        };
    }

    private static object ExtractTable(DomNode table)
    {
        var rows = Flatten(table).Where(e => e.TagName == "tr").ToArray();
        var headerCells = Flatten(table)
            .Where(e => e.TagName == "th")
            .Select(VisibleTextOf)
            .ToArray();
        var maxColumns = rows
            .Select(r => r.Children.Count(c => c.TagName is "td" or "th"))
            .DefaultIfEmpty(0)
            .Max();

        return new
        {
            rows = rows.Length,
            columns = maxColumns,
            headerCells,
            hasThead = Flatten(table).Any(e => e.TagName == "thead"),
        };
    }

    private static object ExtractLinkRelations(List<DomNode> elements)
    {
        var links = elements
            .Where(e => e.TagName == "link" && Attr(e, "rel") is not null)
            .ToArray();

        return new
        {
            pagination = links
                .Where(e => Attr(e, "rel")!.Contains("next", StringComparison.OrdinalIgnoreCase) ||
                            Attr(e, "rel")!.Contains("prev", StringComparison.OrdinalIgnoreCase))
                .Select(e => new { rel = Attr(e, "rel"), href = Attr(e, "href") })
                .ToArray(),
            alternateFormats = links
                .Where(e => Attr(e, "rel")!.Contains("alternate", StringComparison.OrdinalIgnoreCase) && Attr(e, "hreflang") is null)
                .Select(e => new { type = Attr(e, "type"), href = Attr(e, "href") })
                .ToArray(),
            other = links
                .Where(e =>
                {
                    var rel = Attr(e, "rel")!;
                    return !rel.Contains("next", StringComparison.OrdinalIgnoreCase) &&
                           !rel.Contains("prev", StringComparison.OrdinalIgnoreCase) &&
                           !rel.Contains("alternate", StringComparison.OrdinalIgnoreCase) &&
                           !rel.Contains("canonical", StringComparison.OrdinalIgnoreCase) &&
                           !rel.Contains("icon", StringComparison.OrdinalIgnoreCase) &&
                           !rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase);
                })
                .Select(e => new { rel = Attr(e, "rel"), href = Attr(e, "href") })
                .ToArray(),
        };
    }

    private static bool IsLlmsTxt(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath.EndsWith("/llms.txt", StringComparison.OrdinalIgnoreCase);

    private static object ExtractLlmsStructure(DomNode root)
    {
        // llms.txt is markdown-like plain text; the tolerant parser wraps it in
        // a body/pre structure. Recover RAW lines (newlines preserved — the
        // markdown structure depends on them).
        var builder = new StringBuilder();
        AppendRawVisible(root, builder);
        var lines = builder.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sections = new List<object>();
        var links = new List<object>();
        string? currentHeading = null;
        var sectionLinks = new List<object>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                if (currentHeading is not null)
                {
                    sections.Add(new { heading = currentHeading, links = sectionLinks.ToArray() });
                    sectionLinks = [];
                }

                currentHeading = trimmed.TrimStart('#', ' ');
            }
            else
            {
                var open = trimmed.IndexOf('[');
                var close = trimmed.IndexOf(']');
                var parenOpen = trimmed.IndexOf('(');
                var parenClose = trimmed.IndexOf(')');
                if (open >= 0 && close > open && parenOpen > close && parenClose > parenOpen)
                {
                    var link = new
                    {
                        text = trimmed[(open + 1)..close],
                        url = trimmed[(parenOpen + 1)..parenClose],
                    };
                    links.Add(link);
                    sectionLinks.Add(link);
                }
            }
        }

        if (currentHeading is not null)
        {
            sections.Add(new { heading = currentHeading, links = sectionLinks.ToArray() });
        }

        return new { sections, linkCount = links.Count };
    }

    private static object ExtractConsentState(List<DomNode> elements)
    {
        var markers = new List<string>();
        var platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements)
        {
            var id = Attr(element, "id") ?? string.Empty;
            var className = Attr(element, "class") ?? string.Empty;
            var combined = id + " " + className;
            if (combined.Contains("cookie-consent", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("cookie-banner", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("consent-banner", StringComparison.OrdinalIgnoreCase))
            {
                if (markers.Count < ConsentMarkerBound)
                {
                    markers.Add(element.TagName + "#" + id + "." + className);
                }
            }

            var src = Attr(element, "src") ?? string.Empty;
            foreach (var hint in ConsentPlatformHints)
            {
                if (src.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    platforms.Add(hint);
                }
            }
        }

        return new
        {
            detected = markers.Count > 0 || platforms.Count > 0,
            markers,
            platformHints = platforms.ToArray(),
        };
    }

    private static object ExtractDataLayer(List<DomNode> elements)
    {
        var snippets = new List<string>();
        var pushCount = 0;
        var totalBytes = 0;

        foreach (var script in elements.Where(e => e.TagName == "script" && Attr(e, "src") is null))
        {
            var content = script.TextContent;
            if (!content.Contains("dataLayer", StringComparison.Ordinal))
            {
                continue;
            }

            totalBytes += Encoding.UTF8.GetByteCount(content);
            var searchFrom = 0;
            while (true)
            {
                var index = content.IndexOf("dataLayer", searchFrom, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }

                pushCount++;
                if (snippets.Count < DataLayerMaxSnippets)
                {
                    var length = Math.Min(DataLayerSnippetBound, content.Length - index);
                    snippets.Add(content.Substring(index, length));
                }

                searchFrom = index + "dataLayer".Length;
            }
        }

        return new { pushCount, snippets, totalBytes };
    }

    private static object ExtractSerpMetrics(List<DomNode> elements)
    {
        var head = elements.FirstOrDefault(e => e.TagName == "head");
        var headNodes = head is null ? [] : Flatten(head);
        var title = headNodes.FirstOrDefault(e => e.TagName == "title");
        var titleText = title is null ? string.Empty : VisibleTextOf(title);
        var description = headNodes
            .Where(e => e.TagName == "meta" && string.Equals(Attr(e, "name"), "description", StringComparison.OrdinalIgnoreCase))
            .Select(e => Attr(e, "content") ?? string.Empty)
            .FirstOrDefault() ?? string.Empty;

        // Character-level basics; pixel-width budgets are the content-quality
        // analyzer's finding domain (title/description-pixel-truncation).
        return new
        {
            titleLength = titleText.Length,
            descriptionLength = description.Length,
            titleText,
            descriptionText = description,
        };
    }
}
