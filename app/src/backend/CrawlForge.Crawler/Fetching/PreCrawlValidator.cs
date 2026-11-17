using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Fetching;

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
/// candidate crawl target) and never alters limits resolution.
/// </remarks>
public sealed class PreCrawlValidator : IPreCrawlValidator
{
    private const int BodySnippetChars = 2048;

    private readonly IHttpFetcher _fetcher;
    private readonly IWafChallengeEvaluator? _challengeEvaluator;

    /// <summary>Creates a validator probing through the given fetcher.</summary>
    /// <param name="fetcher">Fetch pipeline used for the seed probe.</param>
    /// <param name="challengeEvaluator">Optional WAF pass run on the probe response.</param>
    /// <exception cref="ArgumentNullException">The fetcher is null.</exception>
    public PreCrawlValidator(IHttpFetcher fetcher, IWafChallengeEvaluator? challengeEvaluator = null)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _challengeEvaluator = challengeEvaluator;
    }

    /// <inheritdoc />
    public async Task<PreCrawlValidationResult> ValidateAsync(string startUrl, CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(startUrl)
            || !Uri.TryCreate(startUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            issues.Add("Start URL must be an absolute http:// or https:// URL.");
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