namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Built-in loopback intercepting proxy (FS-CRAWL-001 §5.9). Opens no
/// non-loopback listener; upstream routing goes only to a user-configured
/// proxy. Captured requests feed the MarTech tag inventory (FS-CRAWL-002 §14).
/// </summary>
public interface ILoopbackInterceptingProxy
{
    /// <summary>Starts the proxy on a loopback endpoint.</summary>
    /// <param name="options">Proxy options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loopback endpoint the proxy listens on.</returns>
    Task<ProxyEndpoint> StartAsync(ProxyOptions options, CancellationToken cancellationToken = default);

    /// <summary>Stops the proxy and releases its listener.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the proxy has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Drains captured requests observed since the previous drain.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Captured requests in observation order.</returns>
    Task<IReadOnlyList<ProxyCapturedRequest>> DrainCapturedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Proxy configuration. The upstream proxy is user-configured and optional.
/// </summary>
/// <param name="UpstreamProxyUrl">Optional user-configured upstream proxy URL.</param>
public sealed record ProxyOptions(string? UpstreamProxyUrl);

/// <summary>
/// A loopback-only proxy endpoint.
/// </summary>
/// <param name="Host">Loopback host name.</param>
/// <param name="Port">Loopback port.</param>
public sealed record ProxyEndpoint(string Host, int Port);

/// <summary>
/// One request observed through the intercepting proxy.
/// </summary>
/// <param name="Url">Requested URL.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="StatusCode">Observed status code.</param>
/// <param name="ContentType">Response content type, when present.</param>
/// <param name="CapturedAt">Capture timestamp (UTC).</param>
public sealed record ProxyCapturedRequest(
    string Url,
    string Method,
    int StatusCode,
    string? ContentType,
    DateTimeOffset CapturedAt);
