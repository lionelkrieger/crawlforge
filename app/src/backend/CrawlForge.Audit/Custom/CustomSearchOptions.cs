namespace CrawlForge.Audit.Custom;

/// <summary>
/// Local configuration of the custom content search (FS-CRAWL-001 §7.16).
/// Constructor-injected implementation choice — the locked
/// <c>CustomSearchItem</c> contract carries key/pattern/regex; these options
/// carry negative terms, targeting, scoping, and the mandatory bounds
/// (regex timeout, matches-per-page cap). Unconfigured options reproduce the
/// documented defaults exactly.
/// </summary>
public sealed record CustomSearchOptions
{
    /// <summary>
    /// Per-item negative terms keyed by item key: when ANY negative term is
    /// present in the target text, the item records no match on that page
    /// ("must be absent for a match"). Terms compare case-insensitively.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NegativeTerms { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Per-item target selection keyed by item key: true searches raw markup, false (default) visible text.</summary>
    public IReadOnlyDictionary<string, bool> RawMarkupTargets { get; init; } =
        new Dictionary<string, bool>();

    /// <summary>Per-item URL-path scoping keyed by item key; absent items run on all paths.</summary>
    public IReadOnlyDictionary<string, PathScope> PathScopes { get; init; } =
        new Dictionary<string, PathScope>();

    /// <summary>Regular-expression execution timeout (mandatory bound).</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum matches recorded per item per page (mandatory bound); excess matches are counted, not stored.</summary>
    public int MaxMatchesPerPage { get; init; } = 1000;

    /// <summary>Maximum stored snippets per item per page.</summary>
    public int MaxSnippetsPerPage { get; init; } = 20;

    /// <summary>Characters of context kept on each side of a match in a snippet.</summary>
    public int SnippetContextChars { get; init; } = 40;

    /// <summary>Validates the option values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is out of range.</exception>
    public void Validate()
    {
        if (RegexTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RegexTimeout), RegexTimeout, "The regex timeout must be positive.");
        }

        if (MaxMatchesPerPage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMatchesPerPage), MaxMatchesPerPage, "The matches-per-page cap must be positive.");
        }

        if (MaxSnippetsPerPage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSnippetsPerPage), MaxSnippetsPerPage, "The snippets-per-page cap must be positive.");
        }

        if (SnippetContextChars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SnippetContextChars), SnippetContextChars, "The snippet context must not be negative.");
        }
    }
}