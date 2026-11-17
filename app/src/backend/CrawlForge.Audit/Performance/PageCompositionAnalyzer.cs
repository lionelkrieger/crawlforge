using System.Globalization;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Page composition metrics (FS-CRAWL-001 §7.11) from the contract DOM tree:
/// total nodes, maximum depth/width, image counts, and the smallest inline
/// font size. The lab probe enriches this with computed-style font sizes and
/// transferred image bytes when a rendered measurement is available.
/// </summary>
public static class PageCompositionAnalyzer
{
    /// <summary>Computes composition metrics from a parsed/rendered document.</summary>
    /// <param name="document">The effective document tree.</param>
    /// <returns>Composition sample (image bytes 0, font size from inline styles).</returns>
    public static PageCompositionSample FromDocument(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var nodes = 0;
        var maxDepth = 0;
        var maxWidth = 0;
        var images = 0;
        double? smallestFont = null;

        void Visit(DomNode node, int depth)
        {
            nodes++;
            if (depth > maxDepth)
            {
                maxDepth = depth;
            }

            if (node.Children.Count > maxWidth)
            {
                maxWidth = node.Children.Count;
            }

            if (string.Equals(node.TagName, "img", StringComparison.OrdinalIgnoreCase))
            {
                images++;
            }

            var fontSize = InlineFontSizePx(node);
            if (fontSize is { } size && (smallestFont is null || size < smallestFont))
            {
                smallestFont = size;
            }

            foreach (var child in node.Children)
            {
                Visit(child, depth + 1);
            }
        }

        Visit(document.Root, 1);
        return new PageCompositionSample(nodes, maxDepth, maxWidth, images, ImageBytes: 0, smallestFont);
    }

    /// <summary>
    /// Merges a DOM-derived sample with a probe sample: probe values win for
    /// measured fields (computed font size, transferred image bytes).
    /// </summary>
    /// <param name="domSample">DOM-derived composition.</param>
    /// <param name="probeSample">Probe composition, when measured.</param>
    /// <returns>The merged composition sample.</returns>
    public static PageCompositionSample Merge(PageCompositionSample domSample, PageCompositionSample? probeSample)
    {
        ArgumentNullException.ThrowIfNull(domSample);
        if (probeSample is null)
        {
            return domSample;
        }

        return new PageCompositionSample(
            probeSample.DomNodes > 0 ? probeSample.DomNodes : domSample.DomNodes,
            probeSample.MaxTreeDepth > 0 ? probeSample.MaxTreeDepth : domSample.MaxTreeDepth,
            probeSample.MaxTreeWidth > 0 ? probeSample.MaxTreeWidth : domSample.MaxTreeWidth,
            probeSample.ImageCount > 0 ? probeSample.ImageCount : domSample.ImageCount,
            probeSample.ImageBytes > 0 ? probeSample.ImageBytes : domSample.ImageBytes,
            probeSample.SmallestFontSizePx ?? domSample.SmallestFontSizePx);
    }

    private static double? InlineFontSizePx(DomNode node)
    {
        string? style = null;
        foreach (var (name, value) in node.Attributes)
        {
            if (string.Equals(name, "style", StringComparison.OrdinalIgnoreCase))
            {
                style = value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = declaration[..separator].Trim();
            if (!string.Equals(name, "font-size", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = declaration[(separator + 1)..].Trim();
            if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            {
                return px;
            }

            if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt))
            {
                return pt * 96.0 / 72.0;
            }
        }

        return null;
    }
}
