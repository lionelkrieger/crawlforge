namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// A classified fetch failure (FS-CRAWL-001 §5.4). The fetch result contract
/// carries successful and HTTP-status outcomes; transport-level and policy
/// failures surface as this exception so the crawl worker can store the
/// classification with the URL record.
/// </summary>
public sealed class FetchFailedException : Exception
{
    /// <summary>Creates an unclassified failure.</summary>
    public FetchFailedException()
        : this(FetchFailureKind.Other, "The fetch failed.")
    {
    }

    /// <summary>Creates an unclassified failure with a message.</summary>
    /// <param name="message">Failure description.</param>
    public FetchFailedException(string message)
        : this(FetchFailureKind.Other, message)
    {
    }

    /// <summary>Creates an unclassified failure with a message and inner cause.</summary>
    /// <param name="message">Failure description.</param>
    /// <param name="innerException">Underlying cause.</param>
    public FetchFailedException(string message, Exception innerException)
        : this(FetchFailureKind.Other, message, innerException)
    {
    }

    /// <summary>Creates a classified failure.</summary>
    /// <param name="kind">Failure classification.</param>
    /// <param name="message">Failure description.</param>
    public FetchFailedException(FetchFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Creates a classified failure with an inner cause.</summary>
    /// <param name="kind">Failure classification.</param>
    /// <param name="message">Failure description.</param>
    /// <param name="innerException">Underlying cause.</param>
    public FetchFailedException(FetchFailureKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Classification of the failure.</summary>
    public FetchFailureKind Kind { get; }
}