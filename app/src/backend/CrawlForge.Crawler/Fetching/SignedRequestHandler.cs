using System.Security.Cryptography;
using System.Text;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Applies user-configured request signing to outbound crawl requests
/// (FS-CRAWL-001 §5.10). Signature headers are applied ONLY to in-scope
/// requests; non-signature headers are retained unchanged; expired material is
/// not applied. Signing secrets are user-owned, encrypted at rest via
/// <c>ISecretProtector</c> (see <see cref="SignedRequestSecretStore"/>), never
/// derived from hardware or OS identity, and never transmitted anywhere except
/// the crawl target (exclusion rows 1/2; criterion #34).
/// </summary>
/// <remarks>
/// <para>
/// Two generic algorithm identifiers are supported (no platform tie-in):
/// <c>static</c> applies the user-supplied header value verbatim (the user
/// pre-computed the signature as a custom request header, §5.1), and
/// <c>hmac-sha256</c> computes HMAC-SHA256 over
/// <c>METHOD SP path-and-query</c> keyed by the secret, emitting
/// <c>keyId:hex-signature</c>. Unknown algorithms are rejected at configuration
/// time.
/// </para>
/// <para>
/// Scope is supplied by the composition root as a predicate over the request
/// URI (wired to the audit scope evaluator); without a predicate every request
/// is treated as in scope. Expiry is consulted through the optional
/// <see cref="SignedRequestExpiryTracker"/>.
/// </para>
/// </remarks>
public sealed class SignedRequestHandler : ISignedRequestHandler
{
    /// <summary>Algorithm identifier: apply the supplied secret value verbatim.</summary>
    public const string StaticAlgorithm = "static";

    /// <summary>Algorithm identifier: compute HMAC-SHA256 over the request line.</summary>
    public const string HmacSha256Algorithm = "hmac-sha256";

    private readonly Func<Uri, bool>? _inScopePredicate;
    private readonly SignedRequestExpiryTracker? _expiryTracker;

    /// <summary>Creates a signing handler.</summary>
    /// <param name="inScopePredicate">
    /// Scope predicate; requests outside scope are returned unchanged. Null
    /// treats every request as in scope.
    /// </param>
    /// <param name="expiryTracker">Optional expiry tracker; expired material is not applied.</param>
    public SignedRequestHandler(
        Func<Uri, bool>? inScopePredicate = null,
        SignedRequestExpiryTracker? expiryTracker = null)
    {
        _inScopePredicate = inScopePredicate;
        _expiryTracker = expiryTracker;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Request or secret is null.</exception>
    /// <exception cref="ArgumentException">The secret's algorithm or header name is invalid.</exception>
    public HttpFetchRequest Sign(HttpFetchRequest request, SignedRequestSecret secret)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(secret);

        if (string.IsNullOrWhiteSpace(secret.HeaderName))
        {
            throw new ArgumentException("The signing secret must name the header carrying the signature.", nameof(secret));
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException($"The request URL '{request.Url}' is not absolute.", nameof(request));
        }

        // In-scope-only application (§5.10): out-of-scope requests pass through untouched.
        if (_inScopePredicate is not null && !_inScopePredicate(uri))
        {
            return request;
        }

        // Expired material is never applied; the user must renew it.
        if (_expiryTracker is not null
            && _expiryTracker.GetState(secret.KeyId, DateTimeOffset.UtcNow) == SignedRequestExpiryState.Expired)
        {
            return request;
        }

        string headerValue = ComputeHeaderValue(request, secret, uri);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers is not null)
        {
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                headers[header.Key] = header.Value;
            }
        }

        headers[secret.HeaderName] = headerValue;
        return request with { Headers = headers };
    }

    private static string ComputeHeaderValue(HttpFetchRequest request, SignedRequestSecret secret, Uri uri)
    {
        if (string.Equals(secret.Algorithm, StaticAlgorithm, StringComparison.OrdinalIgnoreCase))
        {
            return secret.Secret;
        }

        if (string.Equals(secret.Algorithm, HmacSha256Algorithm, StringComparison.OrdinalIgnoreCase))
        {
            string message = request.Method.ToUpperInvariant() + " " + uri.PathAndQuery;
            byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret.Secret), Encoding.UTF8.GetBytes(message));
            return secret.KeyId + ":" + Convert.ToHexString(signature).ToLowerInvariant();
        }

        throw new ArgumentException(
            $"Unknown signing algorithm '{secret.Algorithm}'; supported: '{StaticAlgorithm}', '{HmacSha256Algorithm}'.",
            nameof(secret));
    }
}