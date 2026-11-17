using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Tests.Waf.Support;

/// <summary>
/// Deterministic observed-response facts for the fingerprinting tests: one
/// corroborated challenge response per named provider (served with 200 OK),
/// single-facet variants, and a clean page. All values are fixed constants.
/// </summary>
public static class WafResponses
{
    /// <summary>A clean 200 OK page with no challenge characteristics.</summary>
    /// <returns>Clean response facts.</returns>
    public static WafEvaluationInput Clean() => new(
        "https://example.com/clean",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "nginx",
            ["content-type"] = "text/html; charset=utf-8",
        },
        "<html><head><title>Real page</title></head><body><p>Genuine content about widgets and gadgets.</p></body></html>");

    /// <summary>A Cloudflare interstitial challenge served with 200 OK.</summary>
    /// <returns>Corroborated Cloudflare response facts.</returns>
    public static WafEvaluationInput CloudflareInterstitial() => new(
        "https://example.com/cf",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "cloudflare",
            ["cf-mitigated"] = "challenge",
            ["cf-ray"] = "8a1b2c3d4e5f6a7b",
            ["set-cookie"] = "__cf_bm=token-value; Path=/; HttpOnly",
        },
        "<html><head><title>Just a moment...</title></head><body>" +
        "<script src=\"/cdn-cgi/challenge-platform/scripts.js\"></script>cf-chl-out</body></html>");

    /// <summary>A DataDome interstitial challenge served with 200 OK.</summary>
    /// <returns>Corroborated DataDome response facts.</returns>
    public static WafEvaluationInput DataDomeInterstitial() => new(
        "https://example.com/dd",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-datadome"] = "protected",
            ["set-cookie"] = "datadome=session-token; Path=/",
        },
        "<html><body>Please enable JS and disable any ad blocker" +
        "<script src=\"https://captcha-delivery.com/interstitial/\"></script>datadome</body></html>");

    /// <summary>An Akamai bot-block document served with 200 OK.</summary>
    /// <returns>Corroborated Akamai response facts.</returns>
    public static WafEvaluationInput AkamaiBotBlock() => new(
        "https://example.com/ak",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "AkamaiGHOST",
            ["set-cookie"] = "_abck=token; Path=/, bm_sz=token2; Path=/",
        },
        "<html><body>Access Denied<p>Reference #18.abcd1234</p>akamai</body></html>");

    /// <summary>An Imperva interstitial challenge served with 200 OK.</summary>
    /// <returns>Corroborated Imperva response facts.</returns>
    public static WafEvaluationInput ImpervaInterstitial() => new(
        "https://example.com/imp",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-iinfo"] = "1234-56789",
            ["set-cookie"] = "incap_ses_123=abc; Path=/, visid_incap_123=def; Path=/",
        },
        "<html><body>Request unsuccessful.<p>Incapsula incident ID: 123-456</p>incapsula</body></html>");

    /// <summary>A PerimeterX CAPTCHA challenge served with 200 OK.</summary>
    /// <returns>Corroborated PerimeterX response facts.</returns>
    public static WafEvaluationInput PerimeterXCaptcha() => new(
        "https://example.com/px",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-px-block"] = "1",
            ["set-cookie"] = "_px3=abc; Path=/, _pxhd=def; Path=/",
        },
        "<html><body>Access to this page has been denied<div id=\"px-captcha\"></div>perimeterx</body></html>");

    /// <summary>Cloudflare headers only: a single header facet, no cookie or body evidence.</summary>
    /// <returns>Single-facet response facts.</returns>
    public static WafEvaluationInput CloudflareHeadersOnly() => new(
        "https://example.com/cf-headers",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "cloudflare",
            ["cf-mitigated"] = "challenge",
        },
        "<html><body>Ordinary page content.</body></html>");

    /// <summary>Challenge body markers only: a single body facet.</summary>
    /// <returns>Single-facet response facts.</returns>
    public static WafEvaluationInput ChallengeBodyOnly() => new(
        "https://example.com/body-only",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "nginx",
        },
        "<html><head><title>Just a moment...</title></head><body>challenge-platform cf-chl-out</body></html>");

    /// <summary>Challenge cookie only: a single cookie facet.</summary>
    /// <returns>Single-facet response facts.</returns>
    public static WafEvaluationInput ChallengeCookieOnly() => new(
        "https://example.com/cookie-only",
        200,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "nginx",
            ["set-cookie"] = "__cf_bm=token; Path=/",
        },
        "<html><body>Ordinary page content.</body></html>");

    /// <summary>The Cloudflare interstitial served with a 403 status instead of 200 OK.</summary>
    /// <returns>Non-200 challenge response facts.</returns>
    public static WafEvaluationInput CloudflareWith403()
    {
        var input = CloudflareInterstitial();
        return input with { StatusCode = 403 };
    }

    /// <summary>Extracts the provider label from a verdict (test readability helper).</summary>
    /// <param name="result">The verdict.</param>
    /// <returns>The provider label.</returns>
    public static WafProvider ProviderOf(WafEvaluationResult result) => result.Provider;
}
