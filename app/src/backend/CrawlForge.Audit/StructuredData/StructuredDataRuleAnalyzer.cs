using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Pipeline rule adapter exposing the structured-data engine as a per-URL rule
/// pass (FS-CRAWL-001 §7.2 step 4, §7.13; WP-09-04). Implements the locked
/// <see cref="IAuditRuleAnalyzer"/> rule-host surface directly, so composition
/// is a one-line DI registration plus an <c>AuditRuleHost</c> family add
/// (WP-09-11). The adapter is stateless and thread-safe (pipeline
/// MaxConcurrency=2): every judgment is produced by the SAME
/// <see cref="StructuredDataValidator"/> core the UI-19 structured-data checker
/// tool consumes through <see cref="IStructuredDataValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Finding mapping (one judgment per block, document order):
/// malformed block → <c>structured-data.malformed-block</c> (Error);
/// semantic failure → <c>structured-data.invalid</c> (Error) with the full
/// error taxonomy, named missing-required fields and the rich-result state;
/// valid rich-result feature → <c>structured-data.valid</c> (Pass);
/// valid non-feature type → <c>structured-data.rich-result-ineligible</c>
/// (Notice); missing recommended properties → additive
/// <c>structured-data.missing-recommended</c> (Notice, CAP-B2-076).
/// </para>
/// <para>
/// The rendered document takes precedence over the raw response when present
/// (CR-009 rendered-first convention); head facts are never content-scoped
/// (§4.3). WAF-blocked and non-HTML records are excluded, mirroring the other
/// rule families. No network access ever occurs.
/// </para>
/// </remarks>
public sealed class StructuredDataRuleAnalyzer : IAuditRuleAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly StructuredDataValidator _validator;

    /// <summary>Creates the adapter over the shared structured-data engine.</summary>
    /// <param name="validator">The validator singleton shared with the UI-19 checker tool.</param>
    public StructuredDataRuleAnalyzer(StructuredDataValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys { get; } =
    [
        StructuredDataRuleKeys.StructuredDataMalformedBlock,
        StructuredDataRuleKeys.StructuredDataInvalid,
        StructuredDataRuleKeys.StructuredDataRichResultIneligible,
        StructuredDataRuleKeys.StructuredDataValid,
        StructuredDataRuleKeys.StructuredDataMissingRecommended,
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Url.StatusClass != CrawlStatusClass.Success)
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
        }

        if (context.Url.ContentType is { } contentType &&
            !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
        }

        var document = context.RenderedDocument ?? context.Document;
        if (document is null)
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
        }

        var result = _validator.ValidateDetailed(document);
        var findings = new List<FindingDraft>();
        foreach (var detail in result.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddBlockFindings(detail, context, findings);
        }

        return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
    }

    private static void AddBlockFindings(StructuredDataBlockDetail detail, PageAuditContext context, List<FindingDraft> findings)
    {
        var block = detail.Block;
        var urlId = context.Url.Id;
        var url = context.Url.CanonicalUrl;
        var malformed = block.Errors.Any(e =>
            e.StartsWith(StructuredDataErrorCodes.MalformedBlock + ":", StringComparison.Ordinal));

        if (malformed)
        {
            findings.Add(Finding(StructuredDataRuleKeys.StructuredDataMalformedBlock, FindingSeverity.Error, urlId, new
            {
                url,
                format = block.Format,
                ordinal = detail.Ordinal,
                type = block.Type,
                errors = block.Errors,
                nestedEntityTypes = detail.NestedEntityTypes,
            }, recommendation: true));
        }
        else if (!block.IsValid)
        {
            findings.Add(Finding(StructuredDataRuleKeys.StructuredDataInvalid, FindingSeverity.Error, urlId, new
            {
                url,
                format = block.Format,
                ordinal = detail.Ordinal,
                type = block.Type,
                errors = block.Errors,
                missingRequired = detail.MissingRequired,
                nestedEntityTypes = detail.NestedEntityTypes,
                richResult = RichResultState(block.Type, detail.MissingRequired),
            }, recommendation: true));
        }
        else if (block.RichResultEligible)
        {
            findings.Add(Finding(StructuredDataRuleKeys.StructuredDataValid, FindingSeverity.Pass, urlId, new
            {
                url,
                format = block.Format,
                ordinal = detail.Ordinal,
                type = block.Type,
                richResult = "eligible",
                nestedEntityTypes = detail.NestedEntityTypes,
            }, recommendation: false));
        }
        else
        {
            findings.Add(Finding(StructuredDataRuleKeys.StructuredDataRichResultIneligible, FindingSeverity.Notice, urlId, new
            {
                url,
                format = block.Format,
                ordinal = detail.Ordinal,
                type = block.Type,
                richResult = "ineligible",
                reason = "the type is not a supported search-gallery rich-result feature",
            }, recommendation: true));
        }

        if (detail.MissingRecommended.Count > 0)
        {
            findings.Add(Finding(StructuredDataRuleKeys.StructuredDataMissingRecommended, FindingSeverity.Notice, urlId, new
            {
                url,
                format = block.Format,
                ordinal = detail.Ordinal,
                type = block.Type,
                fields = detail.MissingRecommended,
                reason = "recommended properties improve rich-result appearance but are not required for eligibility",
            }, recommendation: true));
        }
    }

    private static string? RichResultState(string? type, IReadOnlyList<string> missingRequired)
    {
        if (!StructuredDataVocabulary.IsRichResultFeature(type))
        {
            return null;
        }

        return missingRequired.Count > 0 ? "missing-required-field" : "ineligible";
    }

    private static FindingDraft Finding(string ruleKey, FindingSeverity severity, long urlId, object evidence, bool recommendation) =>
        new(ruleKey, AuditCategory.StructuredData, severity, urlId,
            JsonSerializer.Serialize(evidence, EvidenceJson),
            recommendation ? Pipeline.RecommendationKeys.ForRule(ruleKey) : null);
}
