using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility.Rules;

/// <summary>
/// Form-label association family (FS-CRAWL-001 §7.10): every form control
/// needs an accessible name (label association, ARIA naming, or title), and
/// related radio/checkbox groups should be grouped with fieldset/legend.
/// </summary>
internal static class FormLabelRule
{
    /// <summary>Input types that carry their own implicit name or no UI.</summary>
    private static readonly HashSet<string> SelfNamedInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hidden", "button", "submit", "reset", "image",
    };

    /// <summary>Evaluates form-label and fieldset/legend rules.</summary>
    public static void Evaluate(AccessibilityRuleContext context)
    {
        EvaluateControlNames(context);
        EvaluateGrouping(context);
    }

    private static void EvaluateControlNames(AccessibilityRuleContext context)
    {
        foreach (var (node, locator) in context.Elements)
        {
            var tag = node.TagName.ToLowerInvariant();
            var isControl = tag is "input" or "select" or "textarea";
            if (!isControl)
            {
                continue;
            }

            if (tag == "input")
            {
                var inputType = DomTraversal.Attr(node, "type") ?? "text";
                if (SelfNamedInputTypes.Contains(inputType))
                {
                    // button/submit/reset use value; image uses alt (image-alt rule).
                    if (inputType is "button" or "submit" or "reset"
                        && string.IsNullOrWhiteSpace(DomTraversal.Attr(node, "value"))
                        && context.AccessibleName(node) is null)
                    {
                        context.Add(
                            AccessibilityRuleKeys.FormLabel,
                            node,
                            locator,
                            $"Input[type={inputType}] has no value or accessible name.",
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["inputType"] = inputType });
                    }

                    continue;
                }
            }

            if (HasNameSource(context, node))
            {
                continue;
            }

            context.Add(
                AccessibilityRuleKeys.FormLabel,
                node,
                locator,
                $"Form control <{tag}> has no associated label or accessible name.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["element"] = tag,
                    ["inputType"] = tag == "input" ? DomTraversal.Attr(node, "type") ?? "text" : string.Empty,
                    ["name"] = DomTraversal.Attr(node, "name") ?? string.Empty,
                });
        }
    }

    private static bool HasNameSource(AccessibilityRuleContext context, DomNode control)
    {
        // ARIA naming or title.
        if (!string.IsNullOrWhiteSpace(DomTraversal.Attr(control, "aria-label"))
            || !string.IsNullOrWhiteSpace(DomTraversal.Attr(control, "aria-labelledby"))
            || !string.IsNullOrWhiteSpace(DomTraversal.Attr(control, "title")))
        {
            return true;
        }

        // Explicit label[for=id] association with content.
        var id = DomTraversal.Attr(control, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var (label, _) in context.ByTag("label"))
            {
                if (string.Equals(DomTraversal.Attr(label, "for"), id, StringComparison.Ordinal)
                    && (DomTraversal.VisibleText(label).Length > 0 || context.AccessibleName(label) is not null))
                {
                    return true;
                }
            }
        }

        // Wrapping label ancestor.
        foreach (var (label, _) in context.ByTag("label"))
        {
            foreach (var (descendant, _) in DomTraversal.Enumerate(label))
            {
                if (ReferenceEquals(descendant, control))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void EvaluateGrouping(AccessibilityRuleContext context)
    {
        // Group radio/checkbox inputs by name; groups of two or more should
        // sit inside a fieldset with a non-empty legend.
        var groups = new Dictionary<string, List<(DomNode Node, DomLocator Locator)>>(StringComparer.Ordinal);
        foreach (var (node, locator) in context.Elements)
        {
            if (!string.Equals(node.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inputType = DomTraversal.Attr(node, "type");
            if (!string.Equals(inputType, "radio", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(inputType, "checkbox", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = DomTraversal.Attr(node, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = inputType!.ToLowerInvariant() + ":" + name;
            if (!groups.TryGetValue(key, out var members))
            {
                members = [];
                groups[key] = members;
            }

            members.Add((node, locator));
        }

        foreach (var (key, members) in groups)
        {
            if (members.Count < 2)
            {
                continue;
            }

            // A fieldset ancestor with a legend covers the whole group when
            // every member sits inside the same fieldset.
            if (GroupHasFieldsetLegend(context, members))
            {
                continue;
            }

            var (firstNode, firstLocator) = members[0];
            context.Add(
                AccessibilityRuleKeys.FieldsetLegend,
                firstNode,
                firstLocator,
                $"Related {key.Split(':')[0]} inputs named '{key.Split(':')[1]}' are not grouped in a fieldset with a legend.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["group"] = key,
                    ["memberCount"] = members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }

    private static bool GroupHasFieldsetLegend(
        AccessibilityRuleContext context,
        List<(DomNode Node, DomLocator Locator)> members)
    {
        foreach (var (fieldset, _) in context.ByTag("fieldset"))
        {
            var legendText = false;
            var descendants = DomTraversal.Enumerate(fieldset);
            foreach (var (descendant, _) in descendants)
            {
                if (string.Equals(descendant.TagName, "legend", StringComparison.OrdinalIgnoreCase)
                    && DomTraversal.VisibleText(descendant).Length > 0)
                {
                    legendText = true;
                    break;
                }
            }

            if (!legendText)
            {
                continue;
            }

            var allInside = members.All(member =>
                descendants.Any(entry => ReferenceEquals(entry.Node, member.Node)));
            if (allInside)
            {
                return true;
            }
        }

        return false;
    }
}
