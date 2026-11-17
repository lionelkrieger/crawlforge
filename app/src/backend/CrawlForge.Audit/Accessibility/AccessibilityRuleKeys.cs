namespace CrawlForge.Audit.Accessibility;

/// <summary>
/// Stable accessibility finding rule keys (FS-CRAWL-001 §7.10). Keys are
/// additive-only: existing keys are never repurposed. Each key maps to one
/// descriptor in the bundled <see cref="AccessibilityRuleSet"/>.
/// </summary>
public static class AccessibilityRuleKeys
{
    /// <summary>Image alternative text is missing (image family).</summary>
    public const string ImageAlt = "accessibility.image-alt";

    /// <summary>An ARIA role token is not a valid concrete WAI-ARIA role.</summary>
    public const string AriaValidRole = "accessibility.aria-valid-role";

    /// <summary>An ARIA attribute name or enumerated value is invalid.</summary>
    public const string AriaValidAttr = "accessibility.aria-valid-attr";

    /// <summary>A role is missing a required ARIA attribute.</summary>
    public const string AriaRequiredAttr = "accessibility.aria-required-attr";

    /// <summary>An ARIA ID reference does not resolve to an element.</summary>
    public const string AriaIdRef = "accessibility.aria-idref";

    /// <summary>A form control has no accessible name (form family).</summary>
    public const string FormLabel = "accessibility.form-label";

    /// <summary>A radio/checkbox group lacks fieldset/legend grouping.</summary>
    public const string FieldsetLegend = "accessibility.fieldset-legend";

    /// <summary>The page has no main landmark (structure family).</summary>
    public const string LandmarkMain = "accessibility.landmark-main";

    /// <summary>Same-type landmarks are not distinguishably labelled.</summary>
    public const string LandmarkUnique = "accessibility.landmark-unique";

    /// <summary>Heading levels skip a rank (structure family).</summary>
    public const string HeadingOrder = "accessibility.heading-order";

    /// <summary>A heading has no accessible content.</summary>
    public const string HeadingEmpty = "accessibility.heading-empty";

    /// <summary>The page has no h1, or more than one h1.</summary>
    public const string HeadingH1 = "accessibility.heading-h1";

    /// <summary>A link has no discernible purpose text (link family).</summary>
    public const string LinkPurpose = "accessibility.link-purpose";

    /// <summary>A link uses generic purpose text such as "click here".</summary>
    public const string LinkGenericText = "accessibility.link-generic-text";

    /// <summary>The document element has no language attribute (language family).</summary>
    public const string DocumentLanguage = "accessibility.document-language";

    /// <summary>The document language tag is malformed.</summary>
    public const string DocumentLanguageValid = "accessibility.document-language-valid";

    /// <summary>
    /// A low color-contrast signal (contrast family). Render-dependent:
    /// computed-style verification requires the rendered tree.
    /// </summary>
    public const string ColorContrast = "accessibility.color-contrast";
}
