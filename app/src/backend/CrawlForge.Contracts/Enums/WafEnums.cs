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
}
