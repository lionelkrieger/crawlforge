using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Accessibility;

/// <summary>
/// One bundled accessibility rule descriptor (FS-CRAWL-001 §7.10): stable key,
/// rule family, default severity, render dependence, and remediation guidance.
/// </summary>
/// <param name="Key">Stable rule key (see <see cref="AccessibilityRuleKeys"/>).</param>
/// <param name="Family">Rule family for report aggregation.</param>
/// <param name="DefaultSeverity">Default finding severity; user-mappable.</param>
/// <param name="RequiresRendering">
/// True when the check requires the rendered tree (layout/computed style);
/// flagged with a notice when rendering is off.
/// </param>
/// <param name="Remediation">Remediation guidance emitted with every violation.</param>
public sealed record AccessibilityRuleDescriptor(
    string Key,
    string Family,
    FindingSeverity DefaultSeverity,
    bool RequiresRendering,
    string Remediation);

/// <summary>
/// The bundled, versioned accessibility rule set (FS-CRAWL-001 §7.10). Ships
/// as a compiled asset and updates only with product updates — no remote
/// catalogue fetch exists. Rules are individually enable-able and
/// severity-mappable through <see cref="AccessibilityAnalyzerOptions"/>.
/// </summary>
public static class AccessibilityRuleSet
{
    /// <summary>Bundled rule-set version (updates only with product updates).</summary>
    public const string Version = "1.0.0";

    /// <summary>Rule families in report order.</summary>
    public static class Families
    {
        /// <summary>Image alternative text.</summary>
        public const string Images = "images";

        /// <summary>ARIA attribute and role validity.</summary>
        public const string Aria = "aria";

        /// <summary>Form-label association.</summary>
        public const string Forms = "forms";

        /// <summary>Landmark and heading structure.</summary>
        public const string Structure = "structure";

        /// <summary>Link purpose.</summary>
        public const string Links = "links";

        /// <summary>Document language.</summary>
        public const string Language = "language";

        /// <summary>Color-contrast signals.</summary>
        public const string Contrast = "contrast";
    }

    /// <summary>All bundled rules in evaluation order.</summary>
    public static IReadOnlyList<AccessibilityRuleDescriptor> Rules { get; } =
    [
        new(AccessibilityRuleKeys.ImageAlt, Families.Images, FindingSeverity.Error, false,
            "Add an alt attribute describing the image's purpose; use alt=\"\" for purely decorative images."),
        new(AccessibilityRuleKeys.AriaValidRole, Families.Aria, FindingSeverity.Error, false,
            "Use a valid concrete WAI-ARIA role; remove unknown or abstract role tokens."),
        new(AccessibilityRuleKeys.AriaValidAttr, Families.Aria, FindingSeverity.Error, false,
            "Use a defined aria-* attribute with a valid value for its type."),
        new(AccessibilityRuleKeys.AriaRequiredAttr, Families.Aria, FindingSeverity.Warning, false,
            "Add the ARIA attributes required by the element's role."),
        new(AccessibilityRuleKeys.AriaIdRef, Families.Aria, FindingSeverity.Warning, false,
            "Point aria-labelledby/aria-describedby/aria-controls/aria-owns at existing element ids."),
        new(AccessibilityRuleKeys.FormLabel, Families.Forms, FindingSeverity.Error, false,
            "Associate a label element, or provide aria-label/aria-labelledby/title so the control has an accessible name."),
        new(AccessibilityRuleKeys.FieldsetLegend, Families.Forms, FindingSeverity.Warning, false,
            "Group related radio buttons or checkboxes in a fieldset with a legend describing the group."),
        new(AccessibilityRuleKeys.LandmarkMain, Families.Structure, FindingSeverity.Warning, false,
            "Wrap the primary content in a main element or an element with role=\"main\"."),
        new(AccessibilityRuleKeys.LandmarkUnique, Families.Structure, FindingSeverity.Notice, false,
            "Give same-type landmarks distinct accessible names (aria-label/aria-labelledby)."),
        new(AccessibilityRuleKeys.HeadingOrder, Families.Structure, FindingSeverity.Warning, false,
            "Keep heading ranks sequential; do not skip heading levels."),
        new(AccessibilityRuleKeys.HeadingEmpty, Families.Structure, FindingSeverity.Warning, false,
            "Give every heading visible text or an accessible name."),
        new(AccessibilityRuleKeys.HeadingH1, Families.Structure, FindingSeverity.Notice, false,
            "Use exactly one h1 describing the page's primary topic."),
        new(AccessibilityRuleKeys.LinkPurpose, Families.Links, FindingSeverity.Error, false,
            "Give the link discernible text, aria-label, or an image with alternative text."),
        new(AccessibilityRuleKeys.LinkGenericText, Families.Links, FindingSeverity.Notice, false,
            "Replace generic link text with text that describes the destination out of context."),
        new(AccessibilityRuleKeys.DocumentLanguage, Families.Language, FindingSeverity.Error, false,
            "Add a lang attribute with the page's primary language to the html element."),
        new(AccessibilityRuleKeys.DocumentLanguageValid, Families.Language, FindingSeverity.Warning, false,
            "Use a well-formed BCP 47 language tag (for example \"en\" or \"en-GB\")."),
        new(AccessibilityRuleKeys.ColorContrast, Families.Contrast, FindingSeverity.Warning, true,
            "Raise the text/background contrast ratio to at least 4.5:1 (3:1 for large text)."),
    ];

    /// <summary>Looks up a descriptor by stable key.</summary>
    /// <param name="key">Stable rule key.</param>
    /// <returns>The descriptor, or null when the key is unknown.</returns>
    public static AccessibilityRuleDescriptor? Find(string key) =>
        Rules.FirstOrDefault(rule => string.Equals(rule.Key, key, StringComparison.Ordinal));
}
