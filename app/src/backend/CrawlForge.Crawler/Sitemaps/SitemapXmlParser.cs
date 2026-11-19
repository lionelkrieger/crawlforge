using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace CrawlForge.Crawler.Sitemaps;

/// <summary>
/// Tolerant XML sitemap parser (FS-CRAWL-001 §5.4 content-type routing: XML
/// sitemaps are expanded; §5.5: URL entries with declared metadata, sitemap
/// indexes expanded recursively). Handles both the sitemaps.org 0.9 namespace
/// and namespace-less documents by matching element local names. Parsing
/// never throws on hostile input: malformed XML, unknown roots, and missing
/// locations become diagnostics; entry counts are bounded per document.
/// Purely local — no network, no remote schema resolution (exclusion E2).
/// </summary>
public static class SitemapXmlParser
{
    /// <summary>Default per-document entry bound (scale invariant).</summary>
    public const int DefaultMaxEntries = 50_000;

    /// <summary>
    /// Parses a sitemap body into a urlset or sitemapindex document.
    /// </summary>
    /// <param name="xml">Raw sitemap XML body.</param>
    /// <param name="maxEntries">Maximum entries retained per document (at least 1).</param>
    /// <returns>The parsed document when recognizable, plus diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is less than 1.</exception>
    public static SitemapParseResult Parse(string xml, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "The entry bound must be positive.");
        }

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            diagnostics.Add("Sitemap body is empty.");
            return new SitemapParseResult(null, diagnostics);
        }

        XDocument document;
        try
        {
            // DTD processing is prohibited: hostile sitemap bodies must not
            // trigger entity expansion or external resource resolution.
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64L * 1024 * 1024,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            diagnostics.Add($"Sitemap XML is malformed: {ex.Message}");
            return new SitemapParseResult(null, diagnostics);
        }

        XElement? root = document.Root;
        if (root is null)
        {
            diagnostics.Add("Sitemap XML has no root element.");
            return new SitemapParseResult(null, diagnostics);
        }

        return root.Name.LocalName switch
        {
            "urlset" => ParseUrlSet(root, maxEntries, diagnostics),
            "sitemapindex" => ParseIndex(root, maxEntries, diagnostics),
            _ => UnknownRoot(root.Name.LocalName, diagnostics),
        };
    }

    private static SitemapParseResult UnknownRoot(string rootName, List<string> diagnostics)
    {
        diagnostics.Add($"Sitemap root element '{rootName}' is neither urlset nor sitemapindex.");
        return new SitemapParseResult(null, diagnostics);
    }

    private static SitemapParseResult ParseUrlSet(XElement root, int maxEntries, List<string> diagnostics)
    {
        var entries = new List<SitemapUrlEntry>();
        var skippedMissingLoc = 0;
        var truncated = false;

        foreach (XElement url in root.Elements().Where(e => e.Name.LocalName == "url"))
        {
            if (entries.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            var loc = ValueOf(url, "loc");
            if (string.IsNullOrWhiteSpace(loc))
            {
                skippedMissingLoc++;
                continue;
            }

            entries.Add(new SitemapUrlEntry(
                loc.Trim(),
                NullIfEmpty(ValueOf(url, "lastmod")),
                NullIfEmpty(ValueOf(url, "changefreq")),
                ParsePriority(ValueOf(url, "priority"))));
        }

        if (skippedMissingLoc > 0)
        {
            diagnostics.Add($"Skipped {skippedMissingLoc} sitemap <url> entries without a <loc>.");
        }

        if (truncated)
        {
            diagnostics.Add($"Sitemap urlset truncated at the {maxEntries}-entry bound.");
        }

        return new SitemapParseResult(
            new SitemapDocument(SitemapDocumentKind.UrlSet, entries, Array.Empty<string>()),
            diagnostics);
    }

    private static SitemapParseResult ParseIndex(XElement root, int maxEntries, List<string> diagnostics)
    {
        var children = new List<string>();
        var skippedMissingLoc = 0;
        var truncated = false;

        foreach (XElement sitemap in root.Elements().Where(e => e.Name.LocalName == "sitemap"))
        {
            if (children.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            var loc = ValueOf(sitemap, "loc");
            if (string.IsNullOrWhiteSpace(loc))
            {
                skippedMissingLoc++;
                continue;
            }

            children.Add(loc.Trim());
        }

        if (skippedMissingLoc > 0)
        {
            diagnostics.Add($"Skipped {skippedMissingLoc} sitemap <sitemap> entries without a <loc>.");
        }

        if (truncated)
        {
            diagnostics.Add($"Sitemap index truncated at the {maxEntries}-entry bound.");
        }

        return new SitemapParseResult(
            new SitemapDocument(SitemapDocumentKind.SitemapIndex, Array.Empty<SitemapUrlEntry>(), children),
            diagnostics);
    }

    private static string? ValueOf(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? ParsePriority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var priority)
            && priority >= 0.0
            && priority <= 1.0
            ? priority
            : null;
    }
}
