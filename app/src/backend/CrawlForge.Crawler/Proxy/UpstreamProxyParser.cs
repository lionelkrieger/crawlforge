namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// A parsed user-configured upstream proxy (§5.9): HTTP-connect class
/// (http/https) or SOCKS class (socks4/socks5). All values are user-supplied;
/// nothing is discovered or fetched remotely.
/// </summary>
/// <param name="Scheme">Proxy scheme (http, https, socks4, socks5).</param>
/// <param name="Host">Proxy host.</param>
/// <param name="Port">Proxy port.</param>
/// <param name="UserName">Optional user-supplied user name.</param>
/// <param name="Password">Optional user-supplied password.</param>
public sealed record UpstreamProxy(
    string Scheme,
    string Host,
    int Port,
    string? UserName,
    string? Password)
{
    /// <summary>Whether the proxy uses the SOCKS protocol class.</summary>
    public bool IsSocks => Scheme is "socks4" or "socks5";

    /// <summary>Whether credentials were supplied by the user.</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(UserName);
}

/// <summary>
/// Parses and validates the user-configured upstream proxy URL. Invalid
/// configuration is rejected locally at startup — never at a remote service.
/// </summary>
public static class UpstreamProxyParser
{
    /// <summary>
    /// Parses an upstream proxy URL. Supported schemes: http, https (CONNECT
    /// class), socks4, socks5. Default ports: 80/443/1080 respectively.
    /// </summary>
    /// <param name="url">The user-configured proxy URL.</param>
    /// <returns>The parsed proxy.</returns>
    /// <exception cref="ArgumentException">The URL is missing, malformed, or uses an unsupported scheme.</exception>
    public static UpstreamProxy Parse(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Upstream proxy URL '{url}' is not a valid absolute URL.", nameof(url));
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var defaultPort = scheme switch
        {
            "http" => 80,
            "https" => 443,
            "socks4" or "socks5" => 1080,
            _ => throw new ArgumentException(
                $"Upstream proxy scheme '{uri.Scheme}' is not supported; use http, https, socks4, or socks5.",
                nameof(url)),
        };

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException($"Upstream proxy URL '{url}' has no host.", nameof(url));
        }

        string? userName = null;
        string? password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            userName = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        return new UpstreamProxy(
            scheme,
            uri.Host,
            uri.IsDefaultPort ? defaultPort : uri.Port,
            userName,
            password);
    }
}
