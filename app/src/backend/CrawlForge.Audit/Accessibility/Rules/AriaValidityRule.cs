using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility.Rules;

/// <summary>
/// ARIA attribute and role validity family (FS-CRAWL-001 §7.10), evaluated
/// against a bundled WAI-ARIA 1.2 vocabulary: concrete role tokens, defined
/// aria-* attributes with type-correct values, role-required attributes, and
/// resolving ID references. The vocabulary ships with the rule set and
/// updates only with product updates.
/// </summary>
internal static class AriaValidityRule
{
    /// <summary>Concrete (author-usable) WAI-ARIA 1.2 roles.</summary>
    private static readonly HashSet<string> ValidRoles = new(StringComparer.Ordinal)
    {
        "alert", "alertdialog", "application", "article", "banner", "blockquote", "button",
        "caption", "cell", "checkbox", "code", "columnheader", "combobox", "complementary",
        "contentinfo", "definition", "deletion", "dialog", "directory", "document", "emphasis",
        "feed", "figure", "form", "generic", "grid", "gridcell", "group", "heading", "img",
        "insertion", "link", "list", "listbox", "listitem", "log", "main", "marquee", "math",
        "menu", "menubar", "menuitem", "menuitemcheckbox", "menuitemradio", "meter",
        "navigation", "none", "note", "option", "paragraph", "presentation", "progressbar",
        "radio", "radiogroup", "region", "row", "rowgroup", "rowheader", "scrollbar", "search",
        "searchbox", "separator", "slider", "spinbutton", "status", "strong", "subscript",
        "superscript", "switch", "tab", "table", "tablist", "textbox", "time", "timer",
        "toolbar", "tooltip", "tree", "treegrid", "treeitem",
    };

    /// <summary>Defined aria-* attributes (WAI-ARIA 1.2, including deprecated ones).</summary>
    private static readonly HashSet<string> ValidAttributes = new(StringComparer.Ordinal)
    {
        "aria-activedescendant", "aria-atomic", "aria-autocomplete", "aria-braillelabel",
        "aria-brailleroledescription", "aria-busy", "aria-checked", "aria-colcount",
        "aria-colindex", "aria-colindextext", "aria-colspan", "aria-controls", "aria-current",
        "aria-describedby", "aria-description", "aria-details", "aria-disabled",
        "aria-dropeffect", "aria-errormessage", "aria-expanded", "aria-flowto", "aria-grabbed",
        "aria-haspopup", "aria-hidden", "aria-invalid", "aria-keyshortcuts", "aria-label",
        "aria-labelledby", "aria-level", "aria-live", "aria-modal", "aria-multiline",
        "aria-multiselectable", "aria-orientation", "aria-owns", "aria-placeholder",
        "aria-posinset", "aria-pressed", "aria-readonly", "aria-relevant", "aria-required",
        "aria-roledescription", "aria-rowcount", "aria-rowindex", "aria-rowindextext",
        "aria-rowspan", "aria-selected", "aria-setsize", "aria-sort", "aria-valuemax",
        "aria-valuemin", "aria-valuenow", "aria-valuetext",
    };

    /// <summary>Attributes whose value is a true/false/undefined token.</summary>
    private static readonly HashSet<string> TristateAttributes = new(StringComparer.Ordinal)
    {
        "aria-atomic", "aria-busy", "aria-disabled", "aria-expanded", "aria-grabbed",
        "aria-hidden", "aria-modal", "aria-multiline", "aria-multiselectable", "aria-readonly",
        "aria-required", "aria-selected",
    };

    /// <summary>Attributes whose value is an integer.</summary>
    private static readonly HashSet<string> IntegerAttributes = new(StringComparer.Ordinal)
    {
        "aria-colcount", "aria-colindex", "aria-colspan", "aria-level", "aria-posinset",
        "aria-rowcount", "aria-rowindex", "aria-rowspan", "aria-setsize",
    };

    /// <summary>Attributes whose value is a number (integer or decimal).</summary>
    private static readonly HashSet<string> NumberAttributes = new(StringComparer.Ordinal)
    {
        "aria-valuemax", "aria-valuemin", "aria-valuenow",
    };

    /// <summary>Role → required aria attributes (conservative ARIA 1.2 subset).</summary>
    private static readonly Dictionary<string, string[]> RequiredAttributes = new(StringComparer.Ordinal)
    {
        ["checkbox"] = ["aria-checked"],
        ["combobox"] = ["aria-expanded"],
        ["heading"] = ["aria-level"],
        ["menuitemcheckbox"] = ["aria-checked"],
        ["menuitemradio"] = ["aria-checked"],
        ["option"] = ["aria-selected"],
        ["radio"] = ["aria-checked"],
        ["scrollbar"] = ["aria-valuenow"],
        ["slider"] = ["aria-valuenow"],
        ["spinbutton"] = ["aria-valuenow"],
        ["switch"] = ["aria-checked"],
    };

    /// <summary>ID-reference attributes resolved against the document.</summary>
    private static readonly string[] IdRefAttributes =
    [
        "aria-labelledby", "aria-describedby", "aria-controls", "aria-owns",
        "aria-activedescendant", "aria-details", "aria-errormessage", "aria-flowto",
    ];

    /// <summary>Evaluates all ARIA validity rules over the page.</summary>
    public static void Evaluate(AccessibilityRuleContext context)
    {
        foreach (var (node, locator) in context.Elements)
        {
            EvaluateRole(context, node, locator);
            EvaluateAttributes(context, node, locator);
            EvaluateIdRefs(context, node, locator);
        }
    }

    private static void EvaluateRole(AccessibilityRuleContext context, DomNode node, DomLocator locator)
    {
        var role = DomTraversal.Attr(node, "role");
        if (string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        var tokens = role.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (!ValidRoles.Contains(token))
            {
                context.Add(
                    AccessibilityRuleKeys.AriaValidRole,
                    node,
                    locator,
                    $"Unknown or abstract ARIA role token '{token}'.",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["role"] = token });
            }
        }

        // Required attributes for the first recognized concrete role.
        var primaryRole = tokens.FirstOrDefault(ValidRoles.Contains);
        if (primaryRole is not null && RequiredAttributes.TryGetValue(primaryRole, out var required))
        {
            foreach (var attribute in required)
            {
                if (DomTraversal.Attr(node, attribute) is null)
                {
                    context.Add(
                        AccessibilityRuleKeys.AriaRequiredAttr,
                        node,
                        locator,
                        $"Role '{primaryRole}' requires the {attribute} attribute.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = primaryRole,
                            ["missingAttribute"] = attribute,
                        });
                }
            }
        }
    }

    private static void EvaluateAttributes(AccessibilityRuleContext context, DomNode node, DomLocator locator)
    {
        foreach (var (name, value) in node.Attributes)
        {
            if (!name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lowerName = name.ToLowerInvariant();
            if (!ValidAttributes.Contains(lowerName))
            {
                context.Add(
                    AccessibilityRuleKeys.AriaValidAttr,
                    node,
                    locator,
                    $"Unknown ARIA attribute '{name}'.",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["attribute"] = name });
                continue;
            }

            var invalidValue = ValidateValue(lowerName, value);
            if (invalidValue is not null)
            {
                context.Add(
                    AccessibilityRuleKeys.AriaValidAttr,
                    node,
                    locator,
                    $"Attribute '{name}' has an invalid value: {invalidValue}.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["attribute"] = name,
                        ["value"] = value,
                    });
            }
        }
    }

    private static string? ValidateValue(string attribute, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "empty value";
        }

        if (TristateAttributes.Contains(attribute))
        {
            return trimmed is "true" or "false" or "undefined" ? null : $"expected true/false/undefined, got '{trimmed}'";
        }

        if (IntegerAttributes.Contains(attribute))
        {
            return long.TryParse(trimmed, out _) ? null : $"expected an integer, got '{trimmed}'";
        }

        if (NumberAttributes.Contains(attribute))
        {
            return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)
                ? null
                : $"expected a number, got '{trimmed}'";
        }

        switch (attribute)
        {
            case "aria-checked" or "aria-pressed":
                return trimmed is "true" or "false" or "mixed" or "undefined"
                    ? null
                    : $"expected true/false/mixed/undefined, got '{trimmed}'";
            case "aria-live":
                return trimmed is "off" or "assertive" or "polite" ? null : $"expected off/assertive/polite, got '{trimmed}'";
            case "aria-sort":
                return trimmed is "ascending" or "descending" or "none" or "other"
                    ? null
                    : $"expected ascending/descending/none/other, got '{trimmed}'";
            case "aria-orientation":
                return trimmed is "horizontal" or "vertical" or "undefined"
                    ? null
                    : $"expected horizontal/vertical/undefined, got '{trimmed}'";
            case "aria-autocomplete":
                return trimmed is "inline" or "list" or "both" or "none"
                    ? null
                    : $"expected inline/list/both/none, got '{trimmed}'";
            case "aria-current":
                return trimmed is "page" or "step" or "location" or "date" or "time" or "true" or "false"
                    ? null
                    : $"expected page/step/location/date/time/true/false, got '{trimmed}'";
            case "aria-haspopup":
                return trimmed is "true" or "false" or "menu" or "listbox" or "tree" or "grid" or "dialog"
                    ? null
                    : $"expected true/false/menu/listbox/tree/grid/dialog, got '{trimmed}'";
            case "aria-invalid":
                return trimmed is "grammar" or "false" or "spelling" or "true"
                    ? null
                    : $"expected grammar/false/spelling/true, got '{trimmed}'";
            case "aria-relevant":
                var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return tokens.All(token => token is "additions" or "removals" or "text" or "all")
                    ? null
                    : $"expected additions/removals/text/all tokens, got '{trimmed}'";
            default:
                // String/ID/token-list attributes: presence of content is valid.
                return null;
        }
    }

    private static void EvaluateIdRefs(AccessibilityRuleContext context, DomNode node, DomLocator locator)
    {
        foreach (var attribute in IdRefAttributes)
        {
            var value = DomTraversal.Attr(node, attribute);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var id in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!context.ElementsById.ContainsKey(id))
                {
                    context.Add(
                        AccessibilityRuleKeys.AriaIdRef,
                        node,
                        locator,
                        $"Attribute '{attribute}' references missing element id '{id}'.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["attribute"] = attribute,
                            ["missingId"] = id,
                        });
                }
            }
        }
    }
}
