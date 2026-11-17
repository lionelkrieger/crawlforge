using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// HTTP fetcher toward user-configured crawl targets only (FS-CRAWL-001 §5.4,
/// network boundary §2.6): configurable timeouts, transient-failure retries
/// with exponential backoff, response size guard, hop-by-hop redirect
/// following with loop detection and a hop ceiling, TTFB measurement feeding
/// the AIMD observables, and optional proxy routing delegated to the
/// <see cref="ILoopbackInterceptingProxy"/> contract (§5.9).
/// </summary>
/// <remarks>
/// <para>
/// Outbound connections go only to the requested crawl target or, when proxy
/// routing is configured, through the loopback intercepting proxy endpoint
/// (validated loopback-only) — no other destination class exists in this type.
/// HTTP status outcomes (including 4xx/5xx) are returned as results; transport
/// and policy failures throw <see cref="FetchFailedException"/> with a
/// <see cref="FetchFailureKind"/> classification for the URL record.
/// </para>
/// <para>
/// Redirects are followed manually so every hop can be recorded through
/// <see cref="IRedirectChainRecorder"/> when a <see cref="FetchContextScope"/>
/// is set. HTTP 429/503 backoff remains the parent politeness layer's concern
/// (FS-CRAWL-002 §2.5): status responses are returned, and the optional
/// <see cref="IAimdPolitenessController"/> receives a
/// <see cref="HostResponseSample"/> per completed fetch. WAF challenge samples
/// are fed by the fingerprinting pass, not by this type.
/// </para>
/// <para>
/// The injected <see cref="HttpClient"/> must be built with automatic
/// redirects disabled; its timeout is set to infinite because per-hop
/// cancellation controls timeouts. The fetcher disposes only clients it
/// creates itself (the proxied client), never the injected one.
/// </para>
/// </remarks>
public sealed class HttpFetcher : IHttpFetcher, IDisposable
{
    private const int MaxBackoffMilliseconds = 30_000;

    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Disposition",
    };

    private readonly HttpFetcherOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IRedirectChainRecorder? _redirectRecorder;
    private readonly IAimdPolitenessController? _aimdController;
    private readonly ISignedRequestHandler? _signedRequestHandler;
    private readonly SignedRequestSecret? _signingSecret;
    private readonly ILoopbackInterceptingProxy? _interceptingProxy;
    private readonly ProxyOptions? _proxyOptions;
    private readonly SemaphoreSlim _proxyGate = new(1, 1);
    private HttpClient? _proxiedClient;
    private bool _disposed;

    /// <summary>Creates a fetcher with optional cross-module collaborators.</summary>
    /// <param name="options">Local fetch mechanics configuration.</param>
    /// <param name="httpClient">
    /// Direct-connection client (automatic redirects disabled). Injected for
    /// composition and testability; not disposed by the fetcher.
    /// </param>
    /// <param name="redirectRecorder">Optional hop recorder (used when a fetch context is set).</param>
    /// <param name="aimdController">Optional AIMD controller receiving TTFB/status observables.</param>
    /// <param name="signedRequestHandler">Optional signing handler (§5.10).</param>
    /// <param name="signingSecret">Optional user signing secret; requires the handler.</param>
    /// <param name="interceptingProxy">
    /// Optional loopback intercepting proxy (§5.9). When present, the fetcher
    /// starts it through the contract and routes all traffic through its
    /// loopback endpoint; stopping remains the composition root's concern.
    /// </param>
    /// <param name="proxyOptions">Proxy options passed to the contract's StartAsync.</param>
    /// <exception cref="ArgumentNullException">Options or client is null.</exception>
    /// <exception cref="ArgumentException">A signing secret is supplied without a handler.</exception>
    public HttpFetcher(
        HttpFetcherOptions options,
        HttpClient httpClient,
        IRedirectChainRecorder? redirectRecorder = null,
        IAimdPolitenessController? aimdController = null,
        ISignedRequestHandler? signedRequestHandler = null,
        SignedRequestSecret? signingSecret = null,
        ILoopbackInterceptingProxy? interceptingProxy = null,
        ProxyOptions? proxyOptions = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (signingSecret is not null && signedRequestHandler is null)
        {
            throw new ArgumentException("A signing secret requires a signed-request handler.", nameof(signingSecret));
        }

        _redirectRecorder = redirectRecorder;
        _aimdController = aimdController;
        _signedRequestHandler = signedRequestHandler;
        _signingSecret = signingSecret;
        _interceptingProxy = interceptingProxy;
        _proxyOptions = proxyOptions;

        // Per-hop linked cancellation controls timeouts, not the client clock.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The request URL is not an absolute http(s) URL.</exception>
    /// <exception cref="FetchFailedException">A classified, non-retryable (or budget-exhausted) failure.</exception>
    public async Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = ValidateRequest(request);

        HttpFetchRequest effective = _signedRequestHandler is not null && _signingSecret is not null
            ? _signedRequestHandler.Sign(request, _signingSecret)
            : request;

        HttpClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);

        int attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                HttpFetchResult result = await FetchCoreAsync(client, effective, cancellationToken).ConfigureAwait(false);
                RecordAimdSample(result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or FetchFailedException)
            {
                FetchFailedException classified = ClassifyFailure(ex, uri, proxyEnabled: _interceptingProxy is not null);
                if (attempt >= _options.MaxRetryAttempts || !IsTransient(classified.Kind))
                {
                    throw classified;
                }

                attempt++;
                int delay = (int)Math.Min((long)_options.RetryBackoffBaseMilliseconds << attempt, MaxBackoffMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Disposes clients created by the fetcher (never the injected client).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _proxiedClient?.Dispose();
        _proxyGate.Dispose();
    }

    private static Uri ValidateRequest(HttpFetchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            throw new ArgumentException("The request URL must be a non-empty absolute http(s) URL.", nameof(request));
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"The request URL '{request.Url}' is not an absolute http(s) URL; the crawl boundary permits no other scheme.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            throw new ArgumentException("The request method must be non-empty.", nameof(request));
        }

        return uri;
    }

    private async Task<HttpFetchResult> FetchCoreAsync(HttpClient client, HttpFetchRequest request, CancellationToken cancellationToken)
    {
        string currentUrl = request.Url;
        string method = request.Method;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        FetchContext? context = FetchContextScope.Current;
        int hop = 0;

        while (true)
        {
            if (!visited.Add(currentUrl))
            {
                throw new FetchFailedException(FetchFailureKind.RedirectLoop, $"Redirect loop detected at '{currentUrl}'.");
            }

            int timeoutSeconds = request.TimeoutSeconds ?? _options.DefaultTimeoutSeconds;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            Stopwatch hopStopwatch = Stopwatch.StartNew();
            using HttpRequestMessage message = BuildRequestMessage(currentUrl, method, request);
            using HttpResponseMessage response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            TimeSpan ttfb = hopStopwatch.Elapsed;
            int statusCode = (int)response.StatusCode;

            if (request.FollowRedirects
                && FetchStatusClassifier.IsRedirect(statusCode)
                && response.Headers.Location is not null)
            {
                if (hop >= _options.MaxRedirectHops)
                {
                    throw new FetchFailedException(
                        FetchFailureKind.RedirectChainExceeded,
                        $"Redirect chain exceeded the ceiling of {_options.MaxRedirectHops} hops at '{currentUrl}'.");
                }

                string nextUrl = ResolveRedirectTarget(currentUrl, response.Headers.Location);
                if (_redirectRecorder is not null && context is not null)
                {
                    await _redirectRecorder
                        .RecordHopAsync(context.AuditId, context.UrlId, new RedirectHop(hop, currentUrl, nextUrl, statusCode), cancellationToken)
                        .ConfigureAwait(false);
                }

                // Standard user-agent behavior: 303 always switches to GET;
                // 301/302 switch non-GET/HEAD methods to GET.
                if (statusCode == 303
                    || (statusCode is 301 or 302
                        && !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)))
                {
                    method = "GET";
                }

                hop++;
                currentUrl = nextUrl;
                continue;
            }

            byte[] body = await ReadBodyGuardedAsync(response, method, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> headers = FlattenHeaders(response);
            return new HttpFetchResult(currentUrl, statusCode, headers, body, ttfb, totalStopwatch.Elapsed);
        }
    }

    private HttpRequestMessage BuildRequestMessage(string url, string method, HttpFetchRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), url);
        bool hasUserAgent = false;

        if (request.Headers is not null)
        {
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    hasUserAgent = true;
                }

                // The contract carries no request body; content headers are skipped.
                if (ContentHeaderNames.Contains(header.Key))
                {
                    continue;
                }

                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (!hasUserAgent)
        {
            message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        }

        if (request.Cookies is { Count: > 0 })
        {
            string cookieHeader = string.Join("; ", request.Cookies.Select(c => $"{c.Key}={c.Value}"));
            message.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        return message;
    }

    private static string ResolveRedirectTarget(string currentUrl, Uri location)
    {
        var baseUri = new Uri(currentUrl);
        Uri target = location.IsAbsoluteUri ? location : new Uri(baseUri, location);
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
        {
            throw new FetchFailedException(
                FetchFailureKind.Other,
                $"Redirect target '{target}' leaves the http(s) crawl boundary and was not followed.");
        }

        return target.AbsoluteUri;
    }

    private async Task<byte[]> ReadBodyGuardedAsync(HttpResponseMessage response, string method, CancellationToken cancellationToken)
    {
        int statusCode = (int)response.StatusCode;
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) || statusCode is 204 or 304)
        {
            return Array.Empty<byte>();
        }

        if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > _options.MaxResponseBytes)
        {
            throw new FetchFailedException(
                FetchFailureKind.Oversize,
                $"Response body declares {declaredLength} bytes, exceeding the {_options.MaxResponseBytes}-byte guard.");
        }

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _options.MaxResponseBytes)
            {
                throw new FetchFailedException(
                    FetchFailureKind.Oversize,
                    $"Response body exceeded the {_options.MaxResponseBytes}-byte guard.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IReadOnlyDictionary<string, string> FlattenHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return headers;
    }

    private void RecordAimdSample(HttpFetchResult result)
    {
        if (_aimdController is null)
        {
            return;
        }

        if (!Uri.TryCreate(result.FinalUrl, UriKind.Absolute, out Uri? finalUri))
        {
            return;
        }

        // Challenge samples (IsChallenge=true) are fed by the WAF pass itself.
        _aimdController.RecordResponse(
            finalUri.Host,
            new HostResponseSample(result.Ttfb.TotalMilliseconds, result.StatusCode, IsChallenge: false, DateTimeOffset.UtcNow));
    }

    private async Task<HttpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_interceptingProxy is null)
        {
            return _httpClient;
        }

        await _proxyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_proxiedClient is null)
            {
                ProxyEndpoint endpoint = await _interceptingProxy
                    .StartAsync(_proxyOptions ?? new ProxyOptions(null), cancellationToken)
                    .ConfigureAwait(false);
                ValidateLoopbackEndpoint(endpoint);

                var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
                    UseProxy = true,
                    AllowAutoRedirect = false,
                };
                _proxiedClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            }

            return _proxiedClient;
        }
        finally
        {
            _proxyGate.Release();
        }
    }

    private static void ValidateLoopbackEndpoint(ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        bool isLoopback = string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(endpoint.Host, out IPAddress? address) && IPAddress.IsLoopback(address));
        if (!isLoopback)
        {
            throw new ArgumentException(
                $"Intercepting proxy endpoint '{endpoint.Host}:{endpoint.Port}' is not loopback; the proxy boundary forbids non-loopback listeners.",
                nameof(endpoint));
        }
    }

    private static bool IsTransient(FetchFailureKind kind) =>
        kind is FetchFailureKind.NameResolution or FetchFailureKind.Timeout or FetchFailureKind.Network or FetchFailureKind.ProxyFailure;

    private static FetchFailedException ClassifyFailure(Exception ex, Uri uri, bool proxyEnabled)
    {
        if (ex is FetchFailedException already)
        {
            return already;
        }

        if (ex is TaskCanceledException or TimeoutException)
        {
            return new FetchFailedException(FetchFailureKind.Timeout, $"Request to '{uri}' timed out.", ex);
        }

        if (ex is HttpRequestException http)
        {
            FetchFailureKind kind = http.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => FetchFailureKind.NameResolution,
                HttpRequestError.SecureConnectionError => FetchFailureKind.Tls,
                _ => FetchFailureKind.Network,
            };

            if (http.InnerException is AuthenticationException)
            {
                kind = FetchFailureKind.Tls;
            }
            else if (http.InnerException is SocketException socket)
            {
                kind = socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.HostUnreachable or SocketError.NetworkUnreachable
                        => FetchFailureKind.NameResolution,
                    SocketError.TimedOut => FetchFailureKind.Timeout,
                    _ => kind,
                };
            }

            if (kind == FetchFailureKind.Network && proxyEnabled)
            {
                kind = FetchFailureKind.ProxyFailure;
            }

            return new FetchFailedException(kind, $"Request to '{uri}' failed ({kind}).", http);
        }

        if (ex is IOException)
        {
            return new FetchFailedException(FetchFailureKind.Network, $"Request to '{uri}' failed at transport level.", ex);
        }

        return new FetchFailedException(FetchFailureKind.Other, $"Request to '{uri}' failed.", ex);
    }
}