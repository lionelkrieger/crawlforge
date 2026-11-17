namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Query-parameter handling policy during canonicalization (FS-CRAWL-001 §5.1/§5.2).
/// </summary>
public enum QueryParameterPolicy
{
    /// <summary>Keep all parameters except known session trackers.</summary>
    KeepAll = 0,

    /// <summary>Keep only whitelisted parameters (session trackers still stripped).</summary>
    KeepWhitelist = 1,

    /// <summary>Strip every query parameter.</summary>
    StripAll = 2
}

/// <summary>
/// Trailing-slash policy applied consistently during canonicalization
/// (FS-CRAWL-001 §5.2). The default preserves the authored path so baseline
/// behavior is unchanged for unconfigured projects (precedence rule 4).
/// </summary>
public enum TrailingSlashPolicy
{
    /// <summary>Keep the trailing slash exactly as authored.</summary>
    Preserve = 0,

    /// <summary>Append a trailing slash to non-root paths lacking one.</summary>
    Append = 1,

    /// <summary>Remove trailing slashes except on the root path.</summary>
    Strip = 2
}

/// <summary>
/// Local configuration of <see cref="UrlCanonicalizer"/>. Unconfigured options
/// reproduce the baseline exactly: keep-all query policy with session-tracker
/// stripping, preserved trailing slashes, no rewrite rules, no base override.
/// </summary>
public sealed record CanonicalizationOptions
{
    /// <summary>Query-parameter policy (default keep-all).</summary>
    public QueryParameterPolicy QueryPolicy { get; init; } = QueryParameterPolicy.KeepAll;

    /// <summary>Whitelisted parameter names when <see cref="QueryPolicy"/> is KeepWhitelist.</summary>
    public IReadOnlyList<string> QueryWhitelist { get; init; } = [];

    /// <summary>Trailing-slash policy (default preserve).</summary>
    public TrailingSlashPolicy TrailingSlash { get; init; } = TrailingSlashPolicy.Preserve;

    /// <summary>
    /// Additional session-tracking parameter names to strip, extending the bundled
    /// local set. Bundled defaults are always applied (FS-CRAWL-001 §5.2).
    /// </summary>
    public IReadOnlyList<string> AdditionalSessionParameters { get; init; } = [];

    /// <summary>
    /// Ordered rewrite rules applied during canonicalization before seen-set keying
    /// (FS-CRAWL-002 §10.4). Empty by default.
    /// </summary>
    public IReadOnlyList<Contracts.Crawl.UrlRewriteRule> RewriteRules { get; init; } = [];

    /// <summary>
    /// Default document base used to resolve relative references when no explicit
    /// base is supplied. Null means relative input without an explicit base is rejected.
    /// </summary>
    public string? DefaultBase { get; init; }
}
