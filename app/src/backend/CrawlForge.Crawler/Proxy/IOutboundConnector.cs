namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// Opens outbound byte tunnels to crawl-traffic destinations. The only
/// implementations connect directly to user-configured crawl targets or chain
/// through the user-configured upstream proxy (§5.9, network boundary §2.6).
/// Injected so tests can prove idle-state zero outbound (criterion #3).
/// </summary>
public interface IOutboundConnector
{
    /// <summary>
    /// Opens a connected byte tunnel to the destination, honoring routing
    /// settings (upstream chaining, per-host bypass, optional direct fallback).
    /// The caller owns and disposes the returned stream.
    /// </summary>
    /// <param name="host">Destination host.</param>
    /// <param name="port">Destination port.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected, owned stream.</returns>
    Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
}
