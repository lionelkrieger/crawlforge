using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Mobile-friendliness analyzer (FS-CRAWL-001 §7.14, optional category):
/// viewport meta presence/validity, font legibility, responsive-design signals,
/// tap-target sizing/spacing, and content-to-viewport fit. Static checks run on
/// the response document; render-dependent checks run on the rendered document
/// and are flagged with a notice when rendering is off.
/// </summary>
/// <remarks>
/// The contract DOM carries no layout geometry, so rendered checks derive from
/// explicit dimensions (width/height attributes and inline px styles) and
/// sibling margins — deterministic, offline heuristics over the stored DOM.
/// Defaults emulate a 390 px mobile viewport with 48 px tap targets and 12 px
/// minimum font size (implementation choice per §7.14).
/// </remarks>
public sealed class MobileFriendlinessAnalyzer : IMobileFriendlinessAnalyzer
{
    /// <summary>Emulated mobile viewport width in CSS pixels.</summary>
    public const int DefaultViewportWidth = 390;

    /// <summary>Minimum recommended tap-target dimension in CSS pixels.</summary>
    public const int MinTapTargetSize = 48;

    /// <summary>Minimum recommended spacing between adjacent tap targets.</summary>
    public const int MinTapTargetSpacing = 8;

    /// <summary>Minimum legible font size in CSS pixels.</summary>
    public const double MinFontSizePx = 12;

    private static readonly Regex PxValue = new(@"(?<num>\d+(?:\.\d+)?)\s*px", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FontSizeDeclaration = new(
        @"font-size\s*:\s*(?<num>\d+(?:\.\d+)?)\s*px", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private static readonly string[] TapTargetTags = ["a", "button", "input", "select", "textarea"];

    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var findings = new List<FindingDraft>();
        var urlId = context.Url.Id;
        var document = context.Document ?? context.RenderedDocument;

        if (document is not null)
        {
            var nodes = Walk(document.Root).ToList();
            CheckViewport(nodes, urlId, findings);
            CheckResponsiveSignals(nodes, urlId, findings);
            CheckFontSizes(nodes, urlId, findings);
        }

        if (context.RenderedDocument is not null)
        {
            var rendered = Walk(context.RenderedDocument.Root).ToList();
            CheckTapTargets(rendered, urlId, findings);
            CheckContentFit(rendered, urlId, findings);
        }
        else
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileRenderRequired, FindingSeverity.Notice, urlId, new
            {
                renderingMode = context.Configuration.RenderingMode.ToString(),
                skippedChecks = new[]
                {
                    "tap-target-sizing", "tap-target-spacing", "content-to-viewport-fit",
                },
                reason = "render-dependent mobile checks require a rendered document; enable rendering to evaluate them",
            }));
        }

        return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
    }

    private static void CheckViewport(List<DomNode> nodes, long urlId, List<FindingDraft> findings)
    {
        var viewport = nodes.FirstOrDefault(n =>
            n.TagName == "meta" &&
            n.Attributes.TryGetValue("name", out var name) &&
            string.Equals(name.Trim(), "viewport", StringComparison.OrdinalIgnoreCase));
        if (viewport is null)
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileViewportMissing, FindingSeverity.Warning, urlId, new
            {
                reason = "no viewport meta tag; mobile browsers fall back to a desktop-width layout viewport",
            }));
            return;
        }

        var content = viewport.Attributes.TryGetValue("content", out var raw) ? raw : string.Empty;
        var directives = ParseViewportContent(content);
        var problems = new List<string>();
        if (!directives.TryGetValue("width", out var width) ||
            !string.Equals(width.Trim(), "device-width", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("width must be device-width");
        }

        if (directives.TryGetValue("initial-scale", out var scale) &&
            (!double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var scaleValue) || scaleValue <= 0))
        {
            problems.Add("initial-scale must be a positive number");
        }

        if (directives.TryGetValue("user-scalable", out var scalable) &&
            (string.Equals(scalable.Trim(), "no", StringComparison.OrdinalIgnoreCase) || scalable.Trim() == "0"))
        {
            problems.Add("user-scalable=no prevents pinch zoom");
        }

        if (directives.TryGetValue("maximum-scale", out var maxScale) &&
            double.TryParse(maxScale, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxScaleValue) &&
            maxScaleValue < 2)
        {
            problems.Add("maximum-scale below 2 restricts pinch zoom");
        }

        if (problems.Count > 0)
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileViewportInvalid, FindingSeverity.Warning, urlId, new
            {
                content,
                problems,
            }));
        }
    }

    private static void CheckResponsiveSignals(List<DomNode> nodes, long urlId, List<FindingDraft> findings)
    {
        var hasMediaQuery = nodes.Any(n =>
            (n.TagName == "style" && n.TextContent.Contains("@media", StringComparison.OrdinalIgnoreCase)) ||
            (n.TagName == "link" && n.Attributes.ContainsKey("media")));
        var hasResponsiveImages = nodes.Any(n =>
            n.TagName == "picture" || n.Attributes.ContainsKey("srcset") || n.Attributes.ContainsKey("sizes"));
        if (!hasMediaQuery && !hasResponsiveImages)
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileResponsiveSignalsMissing, FindingSeverity.Notice, urlId, new
            {
                reason = "no media queries, srcset/sizes, or picture elements found in the document",
            }));
        }
    }

    private static void CheckFontSizes(List<DomNode> nodes, long urlId, List<FindingDraft> findings)
    {
        var offenders = new List<double>();
        foreach (var node in nodes)
        {
            if (node.Attributes.TryGetValue("style", out var style))
            {
                foreach (Match match in FontSizeDeclaration.Matches(style))
                {
                    AddIfSmall(match, offenders);
                }
            }

            if (node.TagName == "style")
            {
                foreach (Match match in FontSizeDeclaration.Matches(node.TextContent))
                {
                    AddIfSmall(match, offenders);
                }
            }
        }

        if (offenders.Count > 0)
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileFontTooSmall, FindingSeverity.Warning, urlId, new
            {
                minimumPx = MinFontSizePx,
                declaredSizesPx = offenders.Distinct().OrderBy(v => v).ToList(),
                occurrences = offenders.Count,
            }));
        }
    }

    private static void CheckTapTargets(List<DomNode> nodes, long urlId, List<FindingDraft> findings)
    {
        var small = new List<object>();
        var close = new List<object>();
        foreach (var node in nodes)
        {
            if (!IsTapTarget(node))
            {
                continue;
            }

            var width = ReadDimension(node, "width");
            var height = ReadDimension(node, "height");
            if (width is not null && height is not null && (width < MinTapTargetSize || height < MinTapTargetSize))
            {
                small.Add(new { tag = node.TagName, widthPx = width, heightPx = height });
            }
        }

        // Spacing heuristic: adjacent sibling tap targets with declared inline
        // margins closer than the recommended minimum on the facing axis.
        foreach (var node in nodes)
        {
            var targets = node.Children.Where(IsTapTarget).ToList();
            for (var i = 1; i < targets.Count; i++)
            {
                var previousMargin = ReadMargin(targets[i - 1]);
                var nextMargin = ReadMargin(targets[i]);
                if (previousMargin is not null && nextMargin is not null &&
                    previousMargin + nextMargin < MinTapTargetSpacing)
                {
                    close.Add(new
                    {
                        previous = targets[i - 1].TagName,
                        current = targets[i].TagName,
                        combinedMarginPx = previousMargin + nextMargin,
                    });
                }
            }
        }

        if (small.Count > 0)
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileTapTargetTooSmall, FindingSeverity.Warning, urlId, new
            {
                minimumPx = MinTapTargetSize,
                targets = small.Take(20).ToList(),
                count = small.Count,
            }));
        }

        if (close.Count > 0)
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileTapTargetsTooClose, FindingSeverity.Warning, urlId, new
            {
                minimumSpacingPx = MinTapTargetSpacing,
                pairs = close.Take(20).ToList(),
                count = close.Count,
            }));
        }
    }

    private static void CheckContentFit(List<DomNode> nodes, long urlId, List<FindingDraft> findings)
    {
        var offenders = new List<object>();
        foreach (var node in nodes)
        {
            var width = ReadDimension(node, "width");
            if (width is not null && width > DefaultViewportWidth)
            {
                offenders.Add(new { tag = node.TagName, widthPx = width });
            }
        }

        if (offenders.Count > 0)
        {
            findings.Add(Finding(StructuredDataRuleKeys.MobileContentWiderThanViewport, FindingSeverity.Warning, urlId, new
            {
                viewportWidthPx = DefaultViewportWidth,
                elements = offenders.Take(20).ToList(),
                count = offenders.Count,
            }));
        }
    }

    private static bool IsTapTarget(DomNode node) =>
        TapTargetTags.Contains(node.TagName) ||
        (node.Attributes.TryGetValue("role", out var role) &&
         string.Equals(role.Trim(), "button", StringComparison.OrdinalIgnoreCase));

    private static double? ReadDimension(DomNode node, string name)
    {
        if (node.Attributes.TryGetValue(name, out var attribute) &&
            double.TryParse(attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var attributeValue) &&
            attributeValue >= 0)
        {
            return attributeValue;
        }

        if (node.Attributes.TryGetValue("style", out var style))
        {
            var match = Regex.Match(style, $@"(?:^|;)\s*{name}\s*:\s*(?<num>\d+(?:\.\d+)?)\s*px",
                RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (match.Success &&
                double.TryParse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var styleValue))
            {
                return styleValue;
            }
        }

        return null;
    }

    private static double? ReadMargin(DomNode node)
    {
        if (!node.Attributes.TryGetValue("style", out var style))
        {
            return null;
        }

        var match = Regex.Match(style, @"(?:^|;)\s*margin(?:-(?:left|right|top|bottom))?\s*:\s*(?<num>\d+(?:\.\d+)?)\s*px",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return match.Success &&
            double.TryParse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static Dictionary<string, string> ParseViewportContent(string content)
    {
        var directives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
            {
                directives[part[..separator].Trim()] = part[(separator + 1)..].Trim();
            }
        }

        return directives;
    }

    private static void AddIfSmall(Match match, List<double> offenders)
    {
        if (double.TryParse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) &&
            size < MinFontSizePx)
        {
            offenders.Add(size);
        }
    }

    private static FindingDraft Finding(string ruleKey, FindingSeverity severity, long urlId, object evidence) =>
        new(ruleKey, AuditCategory.StructuredData, severity, urlId, JsonSerializer.Serialize(evidence), null);

    private static IEnumerable<DomNode> Walk(DomNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
