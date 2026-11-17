namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// A proxy routing failure (§5.9): the upstream proxy was unreachable or
/// rejected the tunnel, and direct fallback is disabled. Classified like any
/// other fetch failure by the caller; never reported to any remote service.
/// </summary>
public sealed class ProxyRoutingException : Exception
{
    /// <summary>Creates the failure with a local diagnostic message.</summary>
    /// <param name="message">Local diagnostic description.</param>
    public ProxyRoutingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the failure with a local diagnostic message and inner cause.</summary>
    /// <param name="message">Local diagnostic description.</param>
    /// <param name="innerException">The underlying transport failure.</param>
    public ProxyRoutingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
