namespace CrawlForge.Crawler.Traps;

/// <summary>
/// Structural pattern families of crawler-trap detection (FS-CRAWL-001 §5.14,
/// added by CR-008): parameter-combination explosion, repeating path segments,
/// incrementing date paths, and runs of near-duplicate content hashes. Every
/// tracked URL pattern carries the family of its structural class; a pattern
/// of any family is flagged once its URL count exceeds the configured budget
/// while mostly producing near-duplicate content, or once a long enough run of
/// near-duplicate content hashes is observed.
/// </summary>
public enum TrapPatternFamily
{
    /// <summary>Same path with exploding query-parameter combinations (faceted navigation).</summary>
    ParameterCombination = 0,

    /// <summary>Path with consecutively repeating segments (e.g. /a/b/a/b/a/b).</summary>
    RepeatingPathSegment = 1,

    /// <summary>Path walking incrementing/decrementing date segments (e.g. /calendar/2026/11).</summary>
    IncrementingDatePath = 2,

    /// <summary>Plain path pattern tracked for runs of near-duplicate content hashes.</summary>
    NearDuplicateRun = 3,
}

/// <summary>
/// Local, user-configurable crawler-trap detection thresholds (FS-CRAWL-001
/// §5.14; the §5.1 "Crawler-trap detection" input row). Defaults reproduce the
/// specification: a pattern is flagged once it exceeds <c>500</c> URLs while
/// mostly producing near-duplicate content. All values are local configuration;
/// nothing here contacts any endpoint and no limit varies with remote input.
/// </summary>
/// <param name="Enabled">Detection on/off. Default on (baseline-safe: unconfigured audits detect traps).</param>
/// <param name="PatternUrlBudget">
/// Per-pattern URL budget: a pattern whose discovered URL count exceeds this
/// budget while its near-duplicate ratio is at least <paramref name="NearDuplicateRatio"/>
/// is flagged and throttled. Default 500 (§5.14), configurable.
/// </param>
/// <param name="NearDuplicateRatio">
/// "Mostly near-duplicate" threshold in [0,1]: the share of observed content
/// hashes that repeat a hash already seen within the pattern. Default 0.8.
/// </param>
/// <param name="NearDuplicateRunLength">
/// A run of this many consecutive near-duplicate content observations within
/// one pattern flags the pattern even below the URL budget (the §5.14
/// "runs of near-duplicate content hashes" family). Default 25; 0 disables
/// run-based flagging.
/// </param>
/// <param name="MaxExamples">Maximum example URLs retained per flagged pattern for the finding. Default 5.</param>
/// <param name="MaxTrackedPatterns">
/// Memory bound: maximum distinct patterns tracked per detector. Beyond the
/// bound, new patterns are admitted untracked (global crawl limits still
/// apply). Default 10000.
/// </param>
/// <param name="MaxDistinctHashesPerPattern">
/// Memory bound: maximum distinct content hashes retained per pattern.
/// Default 4096.
/// </param>
public sealed record CrawlerTrapOptions(
    bool Enabled = true,
    int PatternUrlBudget = 500,
    double NearDuplicateRatio = 0.8,
    int NearDuplicateRunLength = 25,
    int MaxExamples = 5,
    int MaxTrackedPatterns = 10_000,
    int MaxDistinctHashesPerPattern = 4096)
{
    /// <summary>Shared baseline options reproducing the §5.14 defaults.</summary>
    public static CrawlerTrapOptions Default { get; } = new();

    /// <summary>Validates the thresholds at configuration time.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A threshold is outside its valid range.</exception>
    public void Validate()
    {
        if (PatternUrlBudget < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PatternUrlBudget), PatternUrlBudget, "The per-pattern URL budget must be at least 1.");
        }

        if (NearDuplicateRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(NearDuplicateRatio), NearDuplicateRatio, "The near-duplicate ratio must be within [0,1].");
        }

        if (NearDuplicateRunLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(NearDuplicateRunLength), NearDuplicateRunLength, "The near-duplicate run length cannot be negative.");
        }

        if (MaxExamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxExamples), MaxExamples, "At least one example URL must be retained.");
        }

        if (MaxTrackedPatterns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTrackedPatterns), MaxTrackedPatterns, "At least one pattern must be trackable.");
        }

        if (MaxDistinctHashesPerPattern < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDistinctHashesPerPattern), MaxDistinctHashesPerPattern, "At least one content hash must be retainable.");
        }
    }
}

/// <summary>
/// Admission decision of the crawler-trap detector for one discovered URL.
/// The frontier consults the detector before enqueueing (after the seen-set
/// deduplication of §5.2); a throttled URL is recorded as skipped with reason
/// <see cref="CrawlerTrapDetector.ThrottleSkipReason"/> and never fetched.
/// </summary>
/// <param name="Admit">Whether the URL may enter the frontier.</param>
/// <param name="SkipReason">Machine-readable skip reason when throttled; otherwise null.</param>
/// <param name="PatternKey">The URL's pattern key, when tracked; otherwise null.</param>
public sealed record TrapAdmission(bool Admit, string? SkipReason, string? PatternKey)
{
    /// <summary>The URL is allowed into the frontier.</summary>
    public static TrapAdmission Allowed { get; } = new(true, null, null);

    /// <summary>Creates a throttle decision for a flagged pattern.</summary>
    /// <param name="patternKey">The flagged pattern key.</param>
    /// <returns>The throttle decision.</returns>
    public static TrapAdmission Throttled(string patternKey) =>
        new(false, CrawlerTrapDetector.ThrottleSkipReason, patternKey);
}

/// <summary>
/// One flagged crawler-trap pattern (FS-CRAWL-001 §5.14): the finding names the
/// pattern, its URL count and examples. Evidence is bounded and local; no
/// identity dimensions are carried (privacy invariant).
/// </summary>
/// <param name="PatternKey">Stable pattern key (masked path / query-stripped URL).</param>
/// <param name="Family">Structural pattern family of the flagged pattern.</param>
/// <param name="UrlCount">Discovered URL count of the pattern at detection time.</param>
/// <param name="AdmittedUrlCount">URLs admitted before the pattern was throttled.</param>
/// <param name="ObservedContentCount">Content observations fed for the pattern.</param>
/// <param name="NearDuplicateCount">Observations whose content hash repeated within the pattern.</param>
/// <param name="NearDuplicateRatio">Near-duplicate share of observations (0 when none observed).</param>
/// <param name="Examples">Bounded example URLs of the pattern, in discovery order.</param>
/// <param name="DetectedAtUtc">Detection timestamp (UTC).</param>
public sealed record TrapDetection(
    string PatternKey,
    TrapPatternFamily Family,
    int UrlCount,
    int AdmittedUrlCount,
    int ObservedContentCount,
    int NearDuplicateCount,
    double NearDuplicateRatio,
    IReadOnlyList<string> Examples,
    DateTimeOffset DetectedAtUtc);

/// <summary>
/// Throttle signal raised when a pattern is flagged (FS-CRAWL-001 §5.14: "a
/// flagged pattern is throttled"). The engine forwards the signal to the
/// host-throttle surface / structured log; the detector itself already
/// throttles admission of the pattern's URLs.
/// </summary>
/// <param name="PatternKey">The flagged pattern key.</param>
/// <param name="Host">Host of the pattern's URLs.</param>
/// <param name="UrlCount">Discovered URL count at detection time.</param>
/// <param name="Budget">The configured per-pattern URL budget.</param>
/// <param name="Action">Throttle action label (always <c>throttled</c>).</param>
public sealed record TrapThrottleSignal(
    string PatternKey,
    string Host,
    int UrlCount,
    int Budget,
    string Action = "throttled");

/// <summary>
/// Additive finding rule keys of crawler-trap detection (FS-CRAWL-001 §5.14).
/// The locked <c>Contracts.Constants.FindingRuleKeys</c> catalogue predates
/// CR-008 trap detection; these stable keys are defined here so the component
/// is self-contained. A serialized contract amendment may append them to the
/// locked catalogue without changing their values.
/// </summary>
public static class CrawlerTrapRuleKeys
{
    /// <summary>A crawler-trap pattern was detected, throttled and reported (§5.14).</summary>
    public const string TrapDetected = "crawler-trap.detected";
}
