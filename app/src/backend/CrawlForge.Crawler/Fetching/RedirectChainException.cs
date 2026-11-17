namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Reason a redirect chain was rejected (FS-CRAWL-001 §5.4, FS-CRAWL-002
/// §10.3: chain-exceeded covers both a hop-ceiling overflow and a loop).
/// </summary>
public enum RedirectChainViolation
{
    /// <summary>The chain revisited a URL (redirect loop).</summary>
    Loop = 0,

    /// <summary>The chain exceeded the configured hop ceiling.</summary>
    ChainCapExceeded = 1,
}

/// <summary>
/// Raised when a redirect chain violates the loop or hop-ceiling policy while
/// being recorded. The crawl worker and the migration matrix validator map
/// this to the chain-exceeded classification.
/// </summary>
public sealed class RedirectChainException : Exception
{
    /// <summary>Creates a chain violation with an unspecified reason.</summary>
    public RedirectChainException()
        : this(RedirectChainViolation.Loop, "The redirect chain is invalid.")
    {
    }

    /// <summary>Creates a chain violation with a message.</summary>
    /// <param name="message">Violation description.</param>
    public RedirectChainException(string message)
        : this(RedirectChainViolation.Loop, message)
    {
    }

    /// <summary>Creates a chain violation with a message and inner cause.</summary>
    /// <param name="message">Violation description.</param>
    /// <param name="innerException">Underlying cause.</param>
    public RedirectChainException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = RedirectChainViolation.Loop;
    }

    /// <summary>Creates a classified chain violation.</summary>
    /// <param name="reason">Violation reason.</param>
    /// <param name="message">Violation description.</param>
    public RedirectChainException(RedirectChainViolation reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Why the chain was rejected.</summary>
    public RedirectChainViolation Reason { get; }
}