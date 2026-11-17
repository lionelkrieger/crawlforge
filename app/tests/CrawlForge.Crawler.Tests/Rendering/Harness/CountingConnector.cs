using CrawlForge.Crawler.Proxy;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Outbound-connector double proving criterion #3 (idle-state zero outbound)
/// and blocking-rule zero outbound: counts connection attempts and optionally
/// fails or delegates to the real connector.
/// </summary>
public sealed class CountingConnector : IOutboundConnector
{
    private readonly IOutboundConnector? _inner;
    private readonly bool _throwOnConnect;
    private int _attempts;

    /// <summary>Creates a counting connector.</summary>
    /// <param name="inner">Optional real connector to delegate to.</param>
    /// <param name="throwOnConnect">When true, any connection attempt fails (idle proof).</param>
    public CountingConnector(IOutboundConnector? inner = null, bool throwOnConnect = false)
    {
        _inner = inner;
        _throwOnConnect = throwOnConnect;
    }

    /// <summary>Number of connection attempts observed.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Hosts requested, in order.</summary>
    public List<string> RequestedHosts { get; } = [];

    /// <inheritdoc />
    public Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _attempts);
        lock (RequestedHosts)
        {
            RequestedHosts.Add($"{host}:{port}");
        }

        if (_throwOnConnect)
        {
            throw new InvalidOperationException("Outbound connection attempted while the proxy must stay idle.");
        }

        if (_inner is null)
        {
            throw new InvalidOperationException("No inner connector configured.");
        }

        return _inner.ConnectAsync(host, port, cancellationToken);
    }
}
