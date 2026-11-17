namespace CrawlForge.Tools.Governance;

/// <summary>A parsed absolute URL (scheme, host, port, path segments).</summary>
internal sealed record ParsedEndpoint(string Scheme, string Host, string Path)
{
    public string[] PathSegments { get; } =
        Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// URL classification for governance rules: loopback detection (the only always-permitted
/// network scope), vendor-host matching and path-sequence matching. Tolerates template
/// holes from interpolated strings (e.g. "http://127.0.0.1:{port}").
/// </summary>
internal static class EndpointClassifier
{
    public static bool TryParse(string? text, out ParsedEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var scheme = value[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https" or "ws" or "wss"))
        {
            return false;
        }

        var rest = value[(schemeEnd + 3)..];
        var hostEnd = rest.IndexOfAny(['/', '?', '#']);
        var authority = hostEnd < 0 ? rest : rest[..hostEnd];
        var path = hostEnd < 0 ? string.Empty : rest[hostEnd..];

        string host;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = authority[1..close].ToLowerInvariant();
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = (colon < 0 ? authority : authority[..colon]).ToLowerInvariant();
        }

        if (host.Length == 0)
        {
            return false;
        }

        endpoint = new ParsedEndpoint(scheme, host, path);
        return true;
    }

    /// <summary>Loopback hosts: localhost, *.localhost, 127.0.0.0/8, ::1.</summary>
    public static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return true;
        }

        if (host is "::1" or "0:0:0:0:0:0:0:1")
        {
            return true;
        }

        return host.StartsWith("127.", StringComparison.Ordinal)
            && host.Split('.').Length == 4
            && host.Split('.').All(part => byte.TryParse(part, out _));
    }

    public static bool IsLoopbackUrl(string? text) =>
        TryParse(text, out var endpoint) && endpoint is not null && IsLoopbackHost(endpoint.Host);

    /// <summary>Same-origin relative URL (renderer fetches to the loopback backend).</summary>
    public static bool IsRelativeUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        return value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>Vendor host match: dot-boundary suffix, or substring for partial labels (e.g. bedrock-runtime).</summary>
    public static bool HostMatches(string host, string vendor)
    {
        if (!vendor.Contains('.', StringComparison.Ordinal))
        {
            return host.Contains(vendor, StringComparison.Ordinal);
        }

        return string.Equals(host, vendor, StringComparison.Ordinal)
            || host.EndsWith("." + vendor, StringComparison.Ordinal);
    }

    public static bool PathContainsSequence(string[] segments, string[] sequence)
    {
        if (sequence.Length == 0 || segments.Length < sequence.Length)
        {
            return false;
        }

        for (var i = 0; i <= segments.Length - sequence.Length; i++)
        {
            var found = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (!string.Equals(segments[i + j], sequence[j], StringComparison.Ordinal))
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }
}
