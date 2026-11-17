using System.Globalization;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility.Rules;

/// <summary>
/// Color-contrast signals (FS-CRAWL-001 §7.10). Render-dependent: evaluates
/// resolvable color/background pairs from inline styles and simple stylesheet
/// rules against the WCAG 2.x contrast ratio (4.5:1 normal text, 3:1 large
/// text). Full computed-style verification requires the rendered tree; when
/// rendering is off the analyzer emits a requires-rendering notice instead.
/// </summary>
internal static partial class ColorContrastRule
{
    private const double NormalTextThreshold = 4.5;
    private const double LargeTextThreshold = 3.0;
    private const double LargeTextPx = 24.0;
    private const double LargeBoldTextPx = 18.66;

    /// <summary>Common CSS named colors (subset sufficient for signals).</summary>
    private static readonly Dictionary<string, (byte R, byte G, byte B)> NamedColors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = (0, 0, 0),
            ["silver"] = (192, 192, 192),
            ["gray"] = (128, 128, 128),
            ["grey"] = (128, 128, 128),
            ["white"] = (255, 255, 255),
            ["maroon"] = (128, 0, 0),
            ["red"] = (255, 0, 0),
            ["purple"] = (128, 0, 128),
            ["fuchsia"] = (255, 0, 255),
            ["green"] = (0, 128, 0),
            ["lime"] = (0, 255, 0),
            ["olive"] = (128, 128, 0),
            ["yellow"] = (255, 255, 0),
            ["navy"] = (0, 0, 128),
            ["blue"] = (0, 0, 255),
            ["teal"] = (0, 128, 128),
            ["aqua"] = (0, 255, 255),
            ["orange"] = (255, 165, 0),
        };

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex StyleRuleRegex();

    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([0-9.]+)\s*)?\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RgbColorRegex();

    /// <summary>Evaluates contrast signals over the rendered tree.</summary>
    public static void Evaluate(AccessibilityRuleContext context)
    {
        // Parent map for background inheritance.
        var parents = new Dictionary<DomNode, DomNode>();
        DomTraversal.Walk(context.Document.Root, (node, _, ancestors) =>
        {
            if (ancestors.Count > 0)
            {
                parents[node] = ancestors[^1];
            }
        });

        // Stylesheet declarations per simple selector (tag / .class / #id).
        var stylesheet = ParseStyleBlocks(context);

        foreach (var (node, locator) in context.Elements)
        {
            if (DomTraversal.VisibleText(node).Length == 0 || HasElementChildren(node))
            {
                // Only leaf text-bearing elements carry a contrast signal.
                continue;
            }

            var declarations = ResolveDeclarations(node, stylesheet);
            var color = ParseColor(GetDeclaration(declarations, "color"));
            var background = ResolveBackground(node, parents, stylesheet);
            if (color is null || background is null)
            {
                continue;
            }

            var ratio = ContrastRatio(color.Value, background.Value);
            var fontSizePx = ParseFontSizePx(GetDeclaration(declarations, "font-size"));
            var bold = IsBold(GetDeclaration(declarations, "font-weight"));
            var largeText = fontSizePx >= LargeTextPx || (bold && fontSizePx >= LargeBoldTextPx);
            var threshold = largeText ? LargeTextThreshold : NormalTextThreshold;
            if (ratio >= threshold)
            {
                continue;
            }

            context.Add(
                AccessibilityRuleKeys.ColorContrast,
                node,
                locator,
                $"Low contrast signal: ratio {ratio.ToString("0.##", CultureInfo.InvariantCulture)}:1 "
                    + $"is below the {threshold.ToString("0.#", CultureInfo.InvariantCulture)}:1 threshold.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ratio"] = ratio.ToString("0.###", CultureInfo.InvariantCulture),
                    ["threshold"] = threshold.ToString("0.#", CultureInfo.InvariantCulture),
                    ["color"] = GetDeclaration(declarations, "color") ?? string.Empty,
                    ["backgroundColor"] = GetDeclaration(declarations, "background-color") ?? string.Empty,
                    ["largeText"] = largeText ? "true" : "false",
                });
        }
    }

    private static bool HasElementChildren(DomNode node) => node.Children.Count > 0;

    private static List<(string Selector, Dictionary<string, string> Declarations)> ParseStyleBlocks(
        AccessibilityRuleContext context)
    {
        var result = new List<(string, Dictionary<string, string>)>();
        foreach (var (styleNode, _) in context.ByTag("style"))
        {
            var css = DomTraversal.VisibleText(styleNode);
            if (css.Length == 0)
            {
                // style content is excluded from visible text; read it raw.
                css = styleNode.TextContent;
            }

            foreach (Match match in StyleRuleRegex().Matches(css))
            {
                var declarations = ParseDeclarations(match.Groups[2].Value);
                foreach (var selector in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IsSimpleSelector(selector))
                    {
                        result.Add((selector, declarations));
                    }
                }
            }
        }

        return result;
    }

    private static bool IsSimpleSelector(string selector) =>
        selector.Length > 0
        && !selector.Contains(' ')
        && !selector.Contains('>')
        && !selector.Contains(':')
        && !selector.Contains('[')
        && !selector.Contains('*');

    private static Dictionary<string, string> ParseDeclarations(string block)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in block.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = declaration[..separator].Trim();
            var value = declaration[(separator + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0)
            {
                declarations[name] = value;
            }
        }

        return declarations;
    }

    private static Dictionary<string, string> ResolveDeclarations(
        DomNode node,
        List<(string Selector, Dictionary<string, string> Declarations)> stylesheet)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (selector, declarations) in stylesheet)
        {
            if (SelectorMatches(selector, node))
            {
                foreach (var (name, value) in declarations)
                {
                    merged[name] = value;
                }
            }
        }

        // Inline styles win over stylesheet rules.
        var inline = DomTraversal.Attr(node, "style");
        if (!string.IsNullOrWhiteSpace(inline))
        {
            foreach (var (name, value) in ParseDeclarations(inline))
            {
                merged[name] = value;
            }
        }

        return merged;
    }

    private static bool SelectorMatches(string selector, DomNode node)
    {
        if (selector.StartsWith('#'))
        {
            return string.Equals(DomTraversal.Attr(node, "id"), selector[1..], StringComparison.Ordinal);
        }

        if (selector.StartsWith('.'))
        {
            var classes = (DomTraversal.Attr(node, "class") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return classes.Contains(selector[1..], StringComparer.Ordinal);
        }

        var tagPart = selector;
        var classPart = (string?)null;
        var dot = selector.IndexOf('.');
        if (dot >= 0)
        {
            tagPart = selector[..dot];
            classPart = selector[(dot + 1)..];
        }

        if (!string.Equals(node.TagName, tagPart, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (classPart is null)
        {
            return true;
        }

        var nodeClasses = (DomTraversal.Attr(node, "class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return nodeClasses.Contains(classPart, StringComparer.Ordinal);
    }

    private static string? GetDeclaration(Dictionary<string, string> declarations, string name) =>
        declarations.TryGetValue(name, out var value) ? value : null;

    private static (byte R, byte G, byte B)? ResolveBackground(
        DomNode node,
        Dictionary<DomNode, DomNode> parents,
        List<(string Selector, Dictionary<string, string> Declarations)> stylesheet)
    {
        // Nearest specified background up the ancestor chain; default white.
        DomNode? current = node;
        while (current is not null)
        {
            var declarations = ResolveDeclarations(current, stylesheet);
            var background = ParseColor(GetDeclaration(declarations, "background-color")
                ?? GetDeclaration(declarations, "background"));
            if (background is not null)
            {
                return background;
            }

            parents.TryGetValue(current, out current);
        }

        return (255, 255, 255);
    }

    private static (byte R, byte G, byte B)? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hexMatch = HexColorRegex().Match(trimmed);
        if (hexMatch.Success)
        {
            var hex = hexMatch.Groups[1].Value;
            if (hex.Length == 3)
            {
                return (
                    Convert.ToByte(new string(hex[0], 2), 16),
                    Convert.ToByte(new string(hex[1], 2), 16),
                    Convert.ToByte(new string(hex[2], 2), 16));
            }

            return (
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }

        var rgbMatch = RgbColorRegex().Match(trimmed);
        if (rgbMatch.Success
            && byte.TryParse(rgbMatch.Groups[1].Value, out var r)
            && byte.TryParse(rgbMatch.Groups[2].Value, out var g)
            && byte.TryParse(rgbMatch.Groups[3].Value, out var b))
        {
            if (rgbMatch.Groups[4].Success
                && double.TryParse(rgbMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
                && alpha < 1.0)
            {
                // Composite over white for translucent colors.
                var clamped = Math.Clamp(alpha, 0.0, 1.0);
                return (
                    (byte)Math.Round(r * clamped + 255 * (1 - clamped)),
                    (byte)Math.Round(g * clamped + 255 * (1 - clamped)),
                    (byte)Math.Round(b * clamped + 255 * (1 - clamped)));
            }

            return (r, g, b);
        }

        return NamedColors.TryGetValue(trimmed, out var named) ? named : null;
    }

    private static double ParseFontSizePx(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 16.0; // browser default
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
        {
            return px;
        }

        if (trimmed.EndsWith("pt", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt))
        {
            return pt * 96.0 / 72.0;
        }

        return 16.0;
    }

    private static bool IsBold(string? value) =>
        value is not null
        && (string.Equals(value.Trim(), "bold", StringComparison.OrdinalIgnoreCase)
            || (int.TryParse(value.Trim(), out var weight) && weight >= 700));

    /// <summary>WCAG 2.x relative luminance.</summary>
    private static double RelativeLuminance((byte R, byte G, byte B) color)
    {
        static double Channel(byte value)
        {
            var srgb = value / 255.0;
            return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG 2.x contrast ratio between two colors.</summary>
    private static double ContrastRatio((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var luminanceA = RelativeLuminance(a);
        var luminanceB = RelativeLuminance(b);
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
