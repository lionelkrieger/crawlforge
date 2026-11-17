using System.Net;

namespace CrawlForge.Security.Handshake;

/// <summary>
/// Loopback-only enforcement for the local backend boundary (FS-CRAWL-001 §2.2:
/// the backend binds the loopback interface only and is never reachable from
/// other machines).
/// </summary>
public static class LoopbackGuard
{
    /// <summary>Returns true when the address is a loopback address (127.0.0.0/8 or ::1).</summary>
    /// <param name="address">Address to inspect.</param>
    /// <returns>True for loopback addresses; false otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public static bool IsLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IPAddress.IsLoopback(address);
    }

    /// <summary>Throws when the address is not a loopback address.</summary>
    /// <param name="address">Address to validate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    /// <exception cref="ArgumentException">The address is not loopback.</exception>
    public static void EnsureLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                $"Address '{address}' is not loopback; the backend boundary permits loopback only.",
                nameof(address));
        }
    }

    /// <summary>Throws when the endpoint is not bound to a loopback address.</summary>
    /// <param name="endpoint">Endpoint to validate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentException">The endpoint address is not loopback.</exception>
    public static void EnsureLoopback(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EnsureLoopback(endpoint.Address);
    }
}