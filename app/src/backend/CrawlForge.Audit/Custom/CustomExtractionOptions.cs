namespace CrawlForge.Audit.Custom;

/// <summary>
/// Local configuration of the custom content extractor (FS-CRAWL-001 §7.9
/// mandatory bounds). Constructor-injected implementation choice — the locked
/// <c>CustomExtractionRule</c> contract carries the per-rule bounds; these
/// options carry the per-page/per-rule execution bounds and path scoping.
/// Unconfigured options reproduce the documented defaults exactly.
/// </summary>
public sealed record CustomExtractionOptions
{
    /// <summary>Per-rule, per-page cap on stored values; excess matches are discarded and counted.</summary>
    public int MaxValuesPerPage { get; init; } = 100;

    /// <summary>Default per-value length cap when a rule sets no MaxLength (truncation is surrogate-safe).</summary>
    public int DefaultValueMaxLength { get; init; } = 10_000;

    /// <summary>Time bound for one rule's evaluation on one page; pathological patterns cannot stall workers.</summary>
    public TimeSpan RuleTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Optional per-rule URL-path scoping keyed by rule name; absent rules run on all paths.</summary>
    public IReadOnlyDictionary<string, PathScope> PathScopes { get; init; } =
        new Dictionary<string, PathScope>();

    /// <summary>Validates the option values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    public void Validate()
    {
        if (MaxValuesPerPage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxValuesPerPage), MaxValuesPerPage, "The per-page value cap must be positive.");
        }

        if (DefaultValueMaxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultValueMaxLength), DefaultValueMaxLength, "The default length cap must be positive.");
        }

        if (RuleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RuleTimeout), RuleTimeout, "The rule timeout must be positive.");
        }
    }
}