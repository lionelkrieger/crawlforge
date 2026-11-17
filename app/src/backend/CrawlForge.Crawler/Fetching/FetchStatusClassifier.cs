using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Status classification for fetch outcomes (FS-CRAWL-001 §5.3/§5.4). HTTP
/// status responses are recorded as results; this helper maps them to the
/// persisted URL status class. Signature-rejection responses (401/403 under
/// configured signed access, §5.10) are identified distinctly so the user can
/// renew the signing material.
/// </summary>
public static class FetchStatusClassifier
{
    /// <summary>Maps a final HTTP status code to the URL status class.</summary>
    /// <param name="statusCode">Final HTTP status code.</param>
    /// <returns>The crawl status class.</returns>
    public static CrawlStatusClass Classify(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => CrawlStatusClass.Success,
        >= 300 and < 400 => CrawlStatusClass.Redirect,
        >= 400 and < 500 => CrawlStatusClass.ClientError,
        >= 500 and < 600 => CrawlStatusClass.ServerError,
        _ => CrawlStatusClass.FetchError,
    };

    /// <summary>
    /// Returns true for status codes that indicate a signature or credential
    /// rejection when signed-request access is configured (§5.10). The crawl
    /// worker combines this with the signing configuration to surface a
    /// distinct renewal diagnostic.
    /// </summary>
    /// <param name="statusCode">Final HTTP status code.</param>
    /// <returns>True for 401 and 403.</returns>
    public static bool IsSignatureRejection(int statusCode) => statusCode is 401 or 403;

    /// <summary>Returns true when the status code is a redirect (3xx).</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <returns>True for 3xx statuses.</returns>
    public static bool IsRedirect(int statusCode) => statusCode is >= 300 and < 400;
}