using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Rendering;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Browser-resolution seam for pre-crawl validation (FS-CRAWL-001 §5.6.3):
/// resolves the LOCAL rendering browser through the same resolution chain the
/// rendering pool uses (pinned bundled Chrome for Testing → user-configured
/// path → local Chrome → local Edge; runtime download is prohibited and nothing
/// is ever launched). Implementations throw
/// <see cref="BrowserResolutionException"/> when no local browser resolves.
/// </summary>
public interface IPreCrawlBrowserResolver
{
    /// <summary>Resolves the local browser identity without launching anything.</summary>
    /// <returns>The resolved local browser.</returns>
    /// <exception cref="BrowserResolutionException">No local browser resolves.</exception>
    ResolvedBrowser ResolveBrowser();
}

/// <summary>
/// Adapts the rendering pool's local browser resolution (§5.6.3,
/// <see cref="HeadlessRenderingPool.ResolveBrowserInfo"/>) to the pre-crawl
/// seam — the resolver the engine's rendering pre-flight hands to
/// <see cref="PreCrawlValidator.ResolveRenderingBrowser"/>.
/// </summary>
public sealed class PoolBrowserResolver : IPreCrawlBrowserResolver
{
    private readonly HeadlessRenderingPool _pool;

    /// <summary>Creates the adapter over a rendering pool.</summary>
    /// <param name="pool">Pool whose local resolution chain is used.</param>
    /// <exception cref="ArgumentNullException">The pool is null.</exception>
    public PoolBrowserResolver(HeadlessRenderingPool pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    /// <inheritdoc />
    public ResolvedBrowser ResolveBrowser() => _pool.ResolveBrowserInfo();
}

/// <summary>
/// Pre-crawl start-URL validation (FS-CRAWL-001 §5.1): resolves and probes the
/// candidate seed before crawling — protocol, redirects, TLS, and login-gate
/// detection — and surfaces blockers before work begins. When a WAF challenge
/// evaluator is supplied, the seed probe runs the fingerprinting pass and a
/// challenge on the seed surfaces as a blocker (FS-CRAWL-002 §2.5).
/// </summary>
/// <remarks>
/// Validation probes through the same <see cref="IHttpFetcher"/> pipeline the
/// crawl uses; it adds no outbound traffic class (the probe goes only to the
/// candidate crawl target) and never alters limits resolution. Rendering-
/// dependent capture modes additionally require a LOCAL browser to resolve
/// first (FS-CRAWL-001 §5.6.3): resolution failure is a validation blocker and
/// non-JS capture remains available. Non-rendering configurations never touch
/// browser resolution.
/// </remarks>
public sealed class PreCrawlValidator : IPreCrawlValidator
{
    // Same local-only resolution chain the rendering pool runs (§5.6.3 —
    // HeadlessRenderingPool.ResolveBrowserInfo over LocalBrowserResolver with
    // default options): bundled CfT → user path → Chrome → Edge. Used when no
    // seam is supplied.
    private static readonly LocalBrowserResolver DefaultBrowserResolver = new();

    private const int BodySnippetChars = 2048;

    private readonly IHttpFetcher _fetcher;
    private readonly IWafChallengeEvaluator? _challengeEvaluator;
    private readonly IPreCrawlBrowserResolver? _browserResolver;

    /// <summary>Creates a validator probing through the given fetcher.</summary>
    /// <param name="fetcher">Fetch pipeline used for the seed probe.</param>
    /// <param name="challengeEvaluator">Optional WAF pass run on the probe response.</param>
    /// <param name="browserResolver">Optional browser-resolution seam (§5.6.3);
    /// when null, rendering-dependent checks run the same
    /// <see cref="LocalBrowserResolver"/> chain the rendering pool uses.</param>
    /// <exception cref="ArgumentNullException">The fetcher is null.</exception>
    public PreCrawlValidator(
        IHttpFetcher fetcher,
        IWafChallengeEvaluator? challengeEvaluator = null,
        IPreCrawlBrowserResolver? browserResolver = null)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _challengeEvaluator = challengeEvaluator;
        _browserResolver = browserResolver;
    }

    /// <inheritdoc />
    public Task<PreCrawlValidationResult> ValidateAsync(string startUrl, CancellationToken cancellationToken = default) =>
        ValidateAsync(startUrl, captureMode: null, captureSettings: null, cancellationToken);

    /// <summary>
    /// Validates a candidate start URL against a capture configuration
    /// (FS-CRAWL-001 §5.6.3): js/dual/adaptive modes and explicit rendered
    /// per-URL-pattern overrides (§5.6.1) must resolve a LOCAL browser before
    /// any crawl work — resolution failure is a validation blocker whose message
    /// names the local resolution chain and keeps non-JS capture available.
    /// Non-rendering configurations never touch browser resolution.
    /// </summary>
    /// <param name="startUrl">Candidate start URL.</param>
    /// <param name="captureMode">The configuration's capture mode (null keeps the
    /// baseline non-JS behaviour).</param>
    /// <param name="captureSettings">The configuration's capture settings, when
    /// any (carries the per-URL-pattern overrides of §5.6.1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation verdict with issues.</returns>
    public async Task<PreCrawlValidationResult> ValidateAsync(
        string startUrl,
        CaptureMode? captureMode,
        CaptureSettings? captureSettings = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(startUrl)
            || !Uri.TryCreate(startUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            issues.Add("Start URL must be an absolute http:// or https:// URL.");
            return new PreCrawlValidationResult(false, issues, null, null);
        }

        // §5.6.3: rendering-dependent capture fails HERE — at pre-crawl
        // validation, with a clear LOCAL error before any network probe — when
        // no browser resolves; non-JS capture remains available. ONE code path
        // with the engine's rendering pre-flight (CheckRenderingBrowser).
        if (CheckRenderingBrowser(captureMode, captureSettings, _browserResolver) is { } browserIssue)
        {
            issues.Add(browserIssue);
            return new PreCrawlValidationResult(false, issues, null, null);
        }

        HttpFetchResult probe;
        try
        {
            probe = await _fetcher
                .FetchAsync(new HttpFetchRequest(startUrl, FollowRedirects: true), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FetchFailedException ex)
        {
            issues.Add(DescribeFailure(ex));
            return new PreCrawlValidationResult(false, issues, null, null);
        }

        bool valid = true;

        if (FetchStatusClassifier.IsSignatureRejection(probe.StatusCode))
        {
            issues.Add($"Login gate or credential rejection at the start URL (HTTP {probe.StatusCode}); supply site credentials or renew signing material.");
            valid = false;
        }
        else if (probe.StatusCode is >= 400 and < 600)
        {
            issues.Add($"Start URL returned HTTP {probe.StatusCode}.");
            valid = false;
        }
        else if (FetchStatusClassifier.IsRedirect(probe.StatusCode))
        {
            issues.Add($"Start URL returned an unresolved redirect (HTTP {probe.StatusCode}).");
            valid = false;
        }

        if (!string.Equals(probe.FinalUrl, startUrl, StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(probe.FinalUrl, UriKind.Absolute, out Uri? finalUri))
            {
                if (!string.Equals(finalUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"Start URL redirects to a different host ('{finalUri.Host}'); the crawl scope derives from the final host.");
                }

                if (uri.Scheme == Uri.UriSchemeHttps && finalUri.Scheme == Uri.UriSchemeHttp)
                {
                    issues.Add("Start URL redirect downgrades HTTPS to HTTP.");
                    valid = false;
                }
            }
        }

        if (valid && probe.StatusCode is >= 200 and < 300 && _challengeEvaluator is not null)
        {
            WafEvaluationResult verdict = _challengeEvaluator.Evaluate(new WafEvaluationInput(
                probe.FinalUrl,
                probe.StatusCode,
                probe.Headers,
                BuildBodySnippet(probe.Body)));
            if (verdict.IsChallenge)
            {
                issues.Add($"WAF challenge detected on the start URL (provider: {verdict.Provider}); crawling would be blocked.");
                valid = false;
            }
        }

        return new PreCrawlValidationResult(valid, issues, probe.FinalUrl, probe.StatusCode);
    }

    /// <summary>
    /// The §5.6.3 pre-crawl browser check as an issue message — ONE code path
    /// for this validator and the engine's rendering pre-flight. Returns the
    /// validation failure message when the capture configuration is rendering-
    /// dependent and no LOCAL browser resolves, else null. Browser resolution
    /// is never touched for non-rendering configurations.
    /// </summary>
    /// <param name="captureMode">The configuration's capture mode (null keeps the
    /// baseline non-JS behaviour).</param>
    /// <param name="captureSettings">The configuration's capture settings, when
    /// any (carries the per-URL-pattern overrides of §5.6.1).</param>
    /// <param name="browserResolver">Browser-resolution seam; null runs the same
    /// <see cref="LocalBrowserResolver"/> chain the rendering pool uses.</param>
    /// <returns>The issue message, or null when the check passes.</returns>
    public static string? CheckRenderingBrowser(
        CaptureMode? captureMode,
        CaptureSettings? captureSettings,
        IPreCrawlBrowserResolver? browserResolver = null) =>
        ResolveRenderingBrowser(captureMode, captureSettings, browserResolver).Issue;

    /// <summary>
    /// The §5.6.3 check with its resolution result: the resolved browser (the
    /// observability fact behind the pre-flight log line) OR the validation
    /// issue message. Both members are null when the capture configuration is
    /// not rendering-dependent — resolution is then never touched; exactly one
    /// member is non-null when it is.
    /// </summary>
    /// <param name="captureMode">The configuration's capture mode (null keeps the
    /// baseline non-JS behaviour).</param>
    /// <param name="captureSettings">The configuration's capture settings, when
    /// any (carries the per-URL-pattern overrides of §5.6.1).</param>
    /// <param name="browserResolver">Browser-resolution seam; null runs the same
    /// <see cref="LocalBrowserResolver"/> chain the rendering pool uses.</param>
    /// <returns>The resolved browser, or the issue message.</returns>
    public static (ResolvedBrowser? Browser, string? Issue) ResolveRenderingBrowser(
        CaptureMode? captureMode,
        CaptureSettings? captureSettings,
        IPreCrawlBrowserResolver? browserResolver = null)
    {
        if (!RequiresRendering(captureMode, captureSettings))
        {
            return (null, null);
        }

        try
        {
            return (browserResolver is not null
                ? browserResolver.ResolveBrowser()
                : DefaultBrowserResolver.ResolveDetailed(), null);
        }
        catch (BrowserResolutionException ex)
        {
            return (null,
                "Rendering-dependent capture cannot start: no local browser resolves "
                + $"({ex.Message}). "
                + "Tried, in order: the pinned bundled Chrome for Testing, a user-configured browser path, "
                + "local Chrome, then local Edge. Non-JS capture remains available.");
        }
    }

    /// <summary>
    /// Whether the capture configuration is rendering-dependent (§5.6.3):
    /// js/dual/adaptive modes, or an explicit rendered per-URL-pattern override
    /// — overrides render matching URLs on top of every capture mode (§5.6.1,
    /// CaptureModeCoordinator semantics).
    /// </summary>
    private static bool RequiresRendering(CaptureMode? captureMode, CaptureSettings? captureSettings)
    {
        if (captureMode is CaptureMode.Js or CaptureMode.Dual or CaptureMode.Adaptive)
        {
            return true;
        }

        if (captureSettings?.PatternOverrides is { } overrides)
        {
            foreach (var patternOverride in overrides)
            {
                if (patternOverride.Capture == PatternCaptureMode.Rendered)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string DescribeFailure(FetchFailedException ex) => ex.Kind switch
    {
        FetchFailureKind.NameResolution => "Start URL host name could not be resolved (DNS failure).",
        FetchFailureKind.Tls => "Start URL TLS negotiation or certificate validation failed.",
        FetchFailureKind.Timeout => "Start URL probe timed out.",
        FetchFailureKind.ProxyFailure => "The configured proxy failed while probing the start URL.",
        FetchFailureKind.RedirectLoop => "Start URL redirect chain contains a loop.",
        FetchFailureKind.RedirectChainExceeded => "Start URL redirect chain exceeds the hop ceiling.",
        FetchFailureKind.Oversize => "Start URL response exceeded the size guard.",
        _ => $"Start URL probe failed ({ex.Kind}): {ex.Message}",
    };

    private static string? BuildBodySnippet(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        string text = Encoding.UTF8.GetString(body.Span);
        return text.Length <= BodySnippetChars ? text : text.Substring(0, BodySnippetChars);
    }
}