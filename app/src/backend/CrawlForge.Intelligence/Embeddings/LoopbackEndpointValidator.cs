using System.Net;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Loopback-only endpoint validation for the user-configured embedding daemon
/// (FS-CRAWL-002 §12.2, exclusion E7, §1.4 rule 9). Configuration validation
/// REJECTS any non-loopback address: only literal loopback IP addresses
/// (127.0.0.0/8, ::1) and the reserved name "localhost" are accepted. Private
/// LAN addresses, public addresses, and arbitrary hostnames are rejected —
/// loopback connections must stay inside the machine boundary. The same
/// validation is re-applied before every request (defense in depth), and
/// "localhost" connections are pinned to the loopback IP without DNS so a
/// poisoned hosts file can never redirect daemon traffic off-machine.
/// </summary>
public static class LoopbackEndpointValidator
{
    /// <summary>
    /// Validates a daemon endpoint URI, throwing when it is not loopback-only.
    /// </summary>
    /// <param name="endpoint">User-configured endpoint (e.g. http://127.0.0.1:11434).</param>
    /// <returns>The validated absolute URI.</returns>
    /// <exception cref="EmbeddingConfigurationException">The endpoint is missing, malformed, or non-loopback.</exception>
    public static Uri Validate(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new EmbeddingConfigurationException("A loopback daemon endpoint is required (e.g. http://127.0.0.1:11434).");
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            throw new EmbeddingConfigurationException($"Endpoint '{endpoint}' is not a valid absolute URI.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new EmbeddingConfigurationException($"Endpoint scheme '{uri.Scheme}' is not permitted; use http or https on loopback.");
        }

        if (!IsLoopbackHost(uri.Host))
        {
            throw new EmbeddingConfigurationException(
                $"Endpoint host '{uri.Host}' is not loopback. Only 127.0.0.0/8, ::1, or localhost are permitted (exclusion E7: remote embedding APIs are forbidden).");
        }

        return uri;
    }

    /// <summary>
    /// Non-throwing validation for configuration surfaces.
    /// </summary>
    /// <param name="endpoint">User-configured endpoint.</param>
    /// <param name="error">Validation error when invalid.</param>
    /// <returns>True when the endpoint is loopback-only.</returns>
    public static bool TryValidate(string? endpoint, out string? error)
    {
        try
        {
            Validate(endpoint);
            error = null;
            return true;
        }
        catch (EmbeddingConfigurationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether a host is a literal loopback address or the reserved localhost name.
    /// No DNS resolution is performed here.
    /// </summary>
    /// <param name="host">URI host component.</param>
    /// <returns>True for loopback-only hosts.</returns>
    public static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Strip IPv6 brackets when present (Uri.Host normally already does).
        var literal = host.Trim('[', ']');
        return IPAddress.TryParse(literal, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Resolves the connection target for a validated loopback URI WITHOUT DNS:
    /// localhost maps to the loopback IP directly; literal addresses pass through.
    /// </summary>
    /// <param name="uri">Validated loopback URI.</param>
    /// <returns>The loopback IP address to connect to.</returns>
    public static IPAddress ResolveLoopbackAddress(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Host.Contains(':') ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        }

        return IPAddress.Parse(uri.Host.Trim('[', ']'));
    }
}
