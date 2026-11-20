using CrawlForge.Contracts.Constants;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Stable finding rule keys for the structured-data area (FS-CRAWL-001 §7.13–§7.15).
/// Rule keys are open strings (schema catalog: findings.rule_key); the keys below
/// are owned by this area and never repurposed. Structured-data and mobile/technology
/// findings use <see cref="Contracts.Enums.AuditCategory.StructuredData"/>;
/// the hreflang/html-lang (i18n) keys moved to
/// <see cref="Contracts.Enums.AuditCategory.Internationalization"/> in phase 10
/// (D-021 — the §7.4 Internationalization row). Keys promoted to the locked
/// <see cref="FindingRuleKeys"/> catalog are aliased here, never re-spelled.
/// </summary>
public static class StructuredDataRuleKeys
{
    /// <summary>
    /// A deprecated term or retired/restricted rich-result type was flagged
    /// from the bundled versioned list (CR-030/D-021). Locked key alias —
    /// the producer emits <see cref="FindingRuleKeys.StructuredDataDeprecatedTerm"/>.
    /// </summary>
    public const string StructuredDataDeprecatedTerm = FindingRuleKeys.StructuredDataDeprecatedTerm;

    /// <summary>A structured-data block is malformed (syntax error).</summary>
    public const string StructuredDataMalformedBlock = "structured-data.malformed-block";

    /// <summary>A structured-data block failed semantic validation.</summary>
    public const string StructuredDataInvalid = "structured-data.invalid";

    /// <summary>A structured-data block is valid but not rich-result eligible.</summary>
    public const string StructuredDataRichResultIneligible = "structured-data.rich-result-ineligible";

    /// <summary>
    /// A typed block is missing recommended (advisory) vocabulary properties
    /// (CAP-B2-076). Notice severity: recommended gaps never invalidate a block
    /// and never block rich-result eligibility; the finding names the fields.
    /// </summary>
    public const string StructuredDataMissingRecommended = "structured-data.missing-recommended";

    /// <summary>Notice annotation: a structured-data block validated cleanly.</summary>
    public const string StructuredDataValid = "structured-data.valid";

    /// <summary>An hreflang annotation carries an invalid language/region code.</summary>
    public const string HreflangInvalidCode = "i18n.hreflang-invalid-code";

    /// <summary>The hreflang set is missing the required self-reference.</summary>
    public const string HreflangMissingSelfReference = "i18n.hreflang-missing-self-reference";

    /// <summary>The same hreflang value is declared more than once.</summary>
    public const string HreflangDuplicate = "i18n.hreflang-duplicate";

    /// <summary>One hreflang value maps to conflicting target URLs.</summary>
    public const string HreflangConflict = "i18n.hreflang-conflict";

    /// <summary>An alternate page does not link back (site-wide reciprocity check).</summary>
    public const string HreflangMissingReciprocity = "i18n.hreflang-missing-reciprocity";

    /// <summary>The html element lacks a lang attribute.</summary>
    public const string HtmlLangMissing = "i18n.html-lang-missing";

    /// <summary>The html lang attribute is not a valid language tag.</summary>
    public const string HtmlLangInvalid = "i18n.html-lang-invalid";

    /// <summary>Notice annotation: the hreflang set passed all page-level checks.</summary>
    public const string HreflangValid = "i18n.hreflang-valid";

    /// <summary>The viewport meta tag is missing.</summary>
    public const string MobileViewportMissing = "mobile.viewport-missing";

    /// <summary>The viewport meta tag is present but invalid or restrictive.</summary>
    public const string MobileViewportInvalid = "mobile.viewport-invalid";

    /// <summary>A tap target is smaller than the minimum recommended size.</summary>
    public const string MobileTapTargetTooSmall = "mobile.tap-target-too-small";

    /// <summary>Adjacent tap targets are closer than the minimum recommended spacing.</summary>
    public const string MobileTapTargetsTooClose = "mobile.tap-targets-too-close";

    /// <summary>Fixed-width content exceeds the emulated mobile viewport.</summary>
    public const string MobileContentWiderThanViewport = "mobile.content-wider-than-viewport";

    /// <summary>Inline font sizes fall below the legibility minimum.</summary>
    public const string MobileFontTooSmall = "mobile.font-too-small";

    /// <summary>No responsive-design signals were found.</summary>
    public const string MobileResponsiveSignalsMissing = "mobile.responsive-signals-missing";

    /// <summary>Notice: render-dependent checks were skipped because rendering is off.</summary>
    public const string MobileRenderRequired = "mobile.render-required";
}

/// <summary>
/// Structured, human-readable error taxonomy for structured-data validation
/// (FS-CRAWL-001 §7.13). Errors are emitted as <c>code: detail</c> strings so the
/// structured-data explorer can group by code while remaining human-readable.
/// </summary>
public static class StructuredDataErrorCodes
{
    /// <summary>The block could not be parsed (malformed JSON or markup).</summary>
    public const string MalformedBlock = "malformed-block";

    /// <summary>A JSON-LD block has no @context.</summary>
    public const string MissingContext = "missing-context";

    /// <summary>The block declares no type (@type / itemtype).</summary>
    public const string MissingType = "missing-type";

    /// <summary>A property required by the vocabulary is absent or empty.</summary>
    public const string MissingRequiredField = "missing-required-field";

    /// <summary>A property value has an invalid format (date, price, URL).</summary>
    public const string InvalidValue = "invalid-value";

    /// <summary>A rich-result feature requirement is not satisfied.</summary>
    public const string RichResultMissingRequired = "rich-result-missing-required";

    /// <summary>Formats one taxonomy error as a stable <c>code: detail</c> string.</summary>
    /// <param name="code">One of the taxonomy codes.</param>
    /// <param name="detail">Human-readable detail.</param>
    /// <returns>The formatted error string.</returns>
    public static string Format(string code, string detail) => $"{code}: {detail}";
}
