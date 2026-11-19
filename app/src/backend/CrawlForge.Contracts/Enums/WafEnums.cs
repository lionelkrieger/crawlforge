namespace CrawlForge.Contracts.Enums;

/// <summary>
/// WAF provider attribution labels for publicly observable challenge behavior
/// (FS-CRAWL-002 §2; clean-room vocabulary per FS-CRAWL-002 §1.2). Detection and
/// reporting only — never challenge solving or bypass (exclusion E3), and verdicts
/// derive solely from the bundled local catalogue plus the observed response
/// (exclusion E4).
/// </summary>
public enum WafProvider
{
    /// <summary>No provider attribution could be established.</summary>
    Unknown = 0,

    /// <summary>Cloudflare challenge behavior.</summary>
    Cloudflare = 1,

    /// <summary>DataDome challenge behavior.</summary>
    DataDome = 2,

    /// <summary>Akamai challenge behavior.</summary>
    Akamai = 3,

    /// <summary>Imperva challenge behavior.</summary>
    Imperva = 4,

    /// <summary>PerimeterX challenge behavior.</summary>
    PerimeterX = 5,

    /// <summary>A provider outside the named labels (including user-local signatures).</summary>
    Other = 6,
}

/// <summary>
/// Challenge page class (FS-CRAWL-002 §1.5: a WAF-served CAPTCHA, interstitial,
/// or bot-block document). Detection and reporting only (exclusion E3).
/// Amendment WP-10-A1 (D-021) appends the two remaining FS-CRAWL-002 §2.3.1
/// catalogue classes — managed challenge and rate-limit soft block. Existing
/// members keep their locked numbers and wire values; the new members use the
/// hyphenated spec wire tokens "managed-challenge" and "rate-limit-soft-block"
/// (see <see cref="EnumWireNames"/>; same convention as CatchUp ↔ "catch-up").
/// </summary>
public enum WafChallengeClass
{
    /// <summary>Challenge observed but class not established.</summary>
    Unknown = 0,

    /// <summary>CAPTCHA challenge document.</summary>
    Captcha = 1,

    /// <summary>Interstitial challenge document (including JavaScript challenges).</summary>
    Interstitial = 2,

    /// <summary>Bot-block document.</summary>
    BotBlock = 3,

    /// <summary>Additive (WP-10-A1, D-021): provider-managed challenge document
    /// (FS-CRAWL-002 §2.3.1 challenge class "managed-challenge"; for example a
    /// managed turnstile-style checkpoint). Wire value: "managed-challenge".</summary>
    ManagedChallenge = 4,

    /// <summary>Additive (WP-10-A1, D-021): rate-limit soft-block document served
    /// with 200 OK (FS-CRAWL-002 §2.3.1 challenge class "rate-limit-soft-block").
    /// Wire value: "rate-limit-soft-block".</summary>
    RateLimitSoftBlock = 5,
}
