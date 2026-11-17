namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// One vendor detection signature: observable script-source markers, inline
/// initialization markers, network endpoint markers, and identifier
/// extraction patterns (first capture group wins).
/// </summary>
/// <param name="Vendor">Vendor classification label.</param>
/// <param name="Kind">Signature kind: container | analytics | consent.</param>
/// <param name="ScriptSrcMarkers">Case-insensitive substrings of script src URLs.</param>
/// <param name="InlineMarkers">Case-insensitive substrings of inline script text.</param>
/// <param name="NetworkMarkers">Case-insensitive substrings of captured request URLs.</param>
/// <param name="IdPatterns">Regex patterns extracting the container/pixel ID (group 1).</param>
public sealed record MarTechSignature(
    string Vendor,
    string Kind,
    string[] ScriptSrcMarkers,
    string[] InlineMarkers,
    string[] NetworkMarkers,
    string[] IdPatterns);

/// <summary>
/// Bundled, versioned MarTech detection catalogue (FS-CRAWL-002 §14.1, the
/// §7.15 asset pattern).
/// <para>
/// PROVENANCE: in-house catalogue authored for WP-04-04 from publicly
/// documented, observable tag artifacts — script URLs, inline initialization
/// markers, and measurement endpoints. No vendor code, SDK, or remote
/// registry content is copied or consulted at build or run time; vendor
/// names are classification labels for detection targets only (clean-room
/// vocabulary). Structure follows the accepted WAF signature catalogue
/// (Crawler\Waf) and technology fingerprint catalogue (Audit\StructuredData)
/// asset patterns. Updated only with product updates; user-defined beacon
/// patterns are local configuration. No remote tag registry or vendor
/// service is ever consulted (E2).
/// </para>
/// </summary>
public static class MarTechCatalogue
{
    /// <summary>Catalogue version (persisted with detections for re-analysis).</summary>
    public const string Version = "1.0.0";

    /// <summary>Signature kind: tag-manager container.</summary>
    public const string KindContainer = "container";

    /// <summary>Signature kind: analytics/advertising tag.</summary>
    public const string KindAnalytics = "analytics";

    /// <summary>Signature kind: consent management platform.</summary>
    public const string KindConsent = "consent";

    /// <summary>All bundled signatures in detection order.</summary>
    public static IReadOnlyList<MarTechSignature> Signatures { get; } =
    [
        new("google-tag-manager", KindContainer,
            ["googletagmanager.com/gtm.js", "googletagmanager.com/gtm/"],
            ["GTM-"],
            ["googletagmanager.com/gtm.js", "googletagmanager.com/gtm/"],
            [@"GTM-[A-Z0-9]{4,10}"]),

        new("google-analytics-4", KindAnalytics,
            ["googletagmanager.com/gtag/js"],
            ["gtag("],
            ["google-analytics.com/g/collect", "google-analytics.com/j/collect", "googletagmanager.com/gtag/js"],
            [@"G-[A-Z0-9]{6,12}"]),

        new("google-ads", KindAnalytics,
            ["googleadservices.com/pagead/conversion", "googleadservices.com/pagead/1p-user-list"],
            ["AW-", "google_conversion_id"],
            ["googleadservices.com/pagead", "google.com/pagead", "doubleclick.net/activity"],
            [@"AW-\d{6,12}"]),

        new("google-analytics-universal", KindAnalytics,
            ["google-analytics.com/analytics.js", "google-analytics.com/ga.js"],
            ["ga('create'", "GoogleAnalyticsObject"],
            ["google-analytics.com/collect", "google-analytics.com/r/collect"],
            [@"UA-\d{4,10}-\d{1,4}"]),

        new("meta-pixel", KindAnalytics,
            ["connect.facebook.net/en_US/fbevents.js", "connect.facebook.net/signals"],
            ["fbq('init'", "fbq(\"init\""],
            ["facebook.com/tr", "connect.facebook.net"],
            [@"fbq\(\s*['""]init['""]\s*,\s*['""](\d{6,20})['""]", @"[?&]id=(\d{6,20})"]),

        new("linkedin-insight", KindAnalytics,
            ["snap.licdn.com/li.lms-analytics"],
            ["_linkedin_partner_id"],
            ["px.ads.linkedin.com", "snap.licdn.com"],
            [@"_linkedin_partner_id\s*=\s*['""]?(\d{4,12})", @"[?&]pid=(\d{4,12})"]),

        new("microsoft-clarity", KindAnalytics,
            ["clarity.ms/tag/"],
            ["clarity('set'", "clarity(\"set\"", "window.clarity"],
            ["clarity.ms/collect", "clarity.ms/tag"],
            [@"clarity\.ms/tag/([a-z0-9]{6,16})"]),

        new("onetrust", KindConsent,
            ["cdn.cookielaw.org", "otSDKStub.js", "otBannerSdk.js"],
            ["OptanonWrapper", "OneTrust"],
            ["cdn.cookielaw.org", "onetrust.com"],
            []),

        new("cookiebot", KindConsent,
            ["consent.cookiebot.com"],
            ["Cookiebot", "CookieConsent"],
            ["consent.cookiebot.com", "cookiebot.com"],
            []),

        new("trustarc", KindConsent,
            ["consent.trustarc.com"],
            ["trustarc"],
            ["consent.trustarc.com", "trustarc.com"],
            []),

        new("quantcast", KindConsent,
            ["quantcast.mgr.consensu.org", "cmp.quantcast.com"],
            ["__tcfapi", "quantcast"],
            ["quantcast.mgr.consensu.org", "cmp.quantcast.com"],
            []),

        new("didomi", KindConsent,
            ["sdk.privacy-center.org"],
            ["Didomi"],
            ["sdk.privacy-center.org", "didomi.io"],
            []),

        new("usercentrics", KindConsent,
            ["app.usercentrics.eu", "privacy-proxy.usercentrics.eu"],
            ["usercentrics", "UC_UI"],
            ["usercentrics.eu"],
            []),

        new("osano", KindConsent,
            ["cmp.osano.com"],
            ["Osano"],
            ["cmp.osano.com", "osano.com"],
            []),
    ];

    /// <summary>Finds a signature by vendor label.</summary>
    /// <param name="vendor">Vendor label.</param>
    /// <returns>The signature, or null.</returns>
    public static MarTechSignature? Find(string vendor) =>
        Signatures.FirstOrDefault(signature =>
            string.Equals(signature.Vendor, vendor, StringComparison.OrdinalIgnoreCase));
}
