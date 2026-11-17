using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility.Rules;

/// <summary>
/// Landmark and heading structure family (FS-CRAWL-001 §7.10): a main
/// landmark exists, same-type landmarks are distinguishable, heading ranks
/// are sequential and non-empty, and the page uses a single h1.
/// </summary>
internal static class StructureRule
{
    /// <summary>Implicit landmark mapping for native sectioning elements.</summary>
    private static readonly Dictionary<string, string> ImplicitLandmarks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = "main",
        ["nav"] = "navigation",
        ["aside"] = "complementary",
        ["header"] = "banner",
        ["footer"] = "contentinfo",
    };

    /// <summary>Evaluates landmark and heading structure rules.</summary>
    public static void Evaluate(AccessibilityRuleContext context)
    {
        EvaluateLandmarks(context);
        EvaluateHeadings(context);
    }

    private static void EvaluateLandmarks(AccessibilityRuleContext context)
    {
        var landmarks = new List<(string Role, DomNode Node, DomLocator Locator)>();
        foreach (var (node, locator) in context.Elements)
        {
            var explicitRole = DomTraversal.Attr(node, "role");
            if (!string.IsNullOrWhiteSpace(explicitRole))
            {
                var primary = explicitRole.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (primary is "main" or "navigation" or "complementary" or "banner" or "contentinfo"
                    or "region" or "form" or "search")
                {
                    landmarks.Add((primary, node, locator));
                    continue;
                }
            }

            if (ImplicitLandmarks.TryGetValue(node.TagName, out var implicitRole))
            {
                // Nested header/footer inside sectioning content are not
                // page-level banner/contentinfo landmarks.
                if ((node.TagName is "header" or "footer") && IsNestedInSectioning(context, node))
                {
                    continue;
                }

                landmarks.Add((implicitRole, node, locator));
            }
        }

        if (!landmarks.Any(landmark => landmark.Role == "main"))
        {
            context.AddDocument(
                AccessibilityRuleKeys.LandmarkMain,
                "The page has no main landmark (main element or role=\"main\").");
        }

        // Same-type landmarks need distinguishable accessible names.
        foreach (var group in landmarks.GroupBy(landmark => landmark.Role, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            var names = members
                .Select(member => LandmarkName(context, member.Node))
                .ToList();
            if (names.Distinct(StringComparer.Ordinal).Count() == members.Count && names.All(name => name.Length > 0))
            {
                continue;
            }

            var firstUnnamed = members.FirstOrDefault(member =>
                LandmarkName(context, member.Node).Length == 0);
            var target = firstUnnamed.Node is null ? members[0] : firstUnnamed;
            context.Add(
                AccessibilityRuleKeys.LandmarkUnique,
                target.Node,
                target.Locator,
                $"Multiple '{group.Key}' landmarks are not distinguishably labelled.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["role"] = group.Key,
                    ["count"] = members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }

    private static bool IsNestedInSectioning(AccessibilityRuleContext context, DomNode node)
    {
        // Walk the ancestor chain via the document tree: sectioning content
        // (article/aside/main/nav/section) demotes header/footer semantics.
        foreach (var (candidate, _) in context.Elements)
        {
            if (!string.Equals(candidate.TagName, "article", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.TagName, "aside", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.TagName, "main", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.TagName, "nav", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.TagName, "section", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (descendant, _) in DomTraversal.Enumerate(candidate))
            {
                if (ReferenceEquals(descendant, node))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Landmark accessible name per the accname contract: aria-labelledby,
    /// then aria-label, then title. Inner text content is NOT a landmark
    /// name (a nav's link text does not label the landmark).
    /// </summary>
    private static string LandmarkName(AccessibilityRuleContext context, DomNode node)
    {
        var labelledby = DomTraversal.Attr(node, "aria-labelledby");
        if (!string.IsNullOrWhiteSpace(labelledby))
        {
            var parts = new List<string>();
            foreach (var id in labelledby.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (context.ElementsById.TryGetValue(id, out var referenced))
                {
                    parts.Add(DomTraversal.VisibleText(referenced));
                }
            }

            var joined = string.Join(" ", parts).Trim();
            if (joined.Length > 0)
            {
                return joined;
            }
        }

        var label = DomTraversal.Attr(node, "aria-label");
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        var title = DomTraversal.Attr(node, "title");
        return string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();
    }

    private static void EvaluateHeadings(AccessibilityRuleContext context)
    {
        var headings = new List<(int Level, DomNode Node, DomLocator Locator)>();
        foreach (var (node, locator) in context.Elements)
        {
            var tag = node.TagName.ToLowerInvariant();
            int level;
            if (tag.Length == 2 && tag[0] == 'h' && tag[1] >= '1' && tag[1] <= '6')
            {
                level = tag[1] - '0';
            }
            else if (string.Equals(DomTraversal.Attr(node, "role"), "heading", StringComparison.OrdinalIgnoreCase))
            {
                var ariaLevel = DomTraversal.Attr(node, "aria-level");
                if (!int.TryParse(ariaLevel, out level) || level < 1 || level > 6)
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            headings.Add((level, node, locator));

            // Empty heading: no visible text and no accessible name.
            if (DomTraversal.VisibleText(node).Length == 0 && context.AccessibleName(node) is null)
            {
                context.Add(
                    AccessibilityRuleKeys.HeadingEmpty,
                    node,
                    locator,
                    $"Heading h{level} has no content or accessible name.",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
        }

        // Sequential ranks: a jump of more than one level is a skip.
        var previousLevel = 0;
        foreach (var (level, node, locator) in headings)
        {
            if (previousLevel > 0 && level > previousLevel + 1)
            {
                context.Add(
                    AccessibilityRuleKeys.HeadingOrder,
                    node,
                    locator,
                    $"Heading level skips from h{previousLevel} to h{level}.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["previousLevel"] = previousLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }

            previousLevel = level;
        }

        var h1Count = headings.Count(heading => heading.Level == 1);
        if (h1Count == 0)
        {
            context.AddDocument(AccessibilityRuleKeys.HeadingH1, "The page has no h1 heading.");
        }
        else if (h1Count > 1)
        {
            context.AddDocument(
                AccessibilityRuleKeys.HeadingH1,
                $"The page has {h1Count} h1 headings; exactly one is recommended.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["h1Count"] = h1Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }
}
