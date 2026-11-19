namespace CrawlForge.Contracts.Enums;

/// <summary>
/// Locked enum wire-name vocabulary of the loopback REST surface (FS-CRAWL-001
/// §10.1: lowercase string enum values). The default wire name of a member is
/// its lowercase CLR name; the locked exceptions are the hyphenated spec tokens
/// listed below. Amendment WP-10-A1 (D-021) appends the capture-mode and
/// WAF-class exceptions without touching any existing wire value.
/// <para>
/// The Api layer's <c>ApiEnumConverter&lt;TEnum&gt;</c> bridges DTO enums to this
/// vocabulary; the frontend mirror lives in
/// <c>app/src/frontend/src/contracts/api/vocabulary.ts</c> and is parity-checked
/// by CrawlForge.Contracts.Tests. Existing wire names are frozen: never rename
/// or repurpose a member's wire value.
/// </para>
/// </summary>
public static class EnumWireNames
{
    /// <summary>ScheduleTriggerType.CatchUp wire token (locked, FS-CRAWL-002 §6).</summary>
    public const string CatchUp = "catch-up";

    /// <summary>WafChallengeClass.ManagedChallenge wire token (FS-CRAWL-002 §2.3.1).</summary>
    public const string ManagedChallenge = "managed-challenge";

    /// <summary>WafChallengeClass.RateLimitSoftBlock wire token (FS-CRAWL-002 §2.3.1).</summary>
    public const string RateLimitSoftBlock = "rate-limit-soft-block";

    /// <summary>CaptureMode.NonJs wire token (FS-CRAWL-001 §5.6.1; hyphenated
    /// per the catch-up convention).</summary>
    public const string NonJsCapture = "non-js";

    /// <summary>PatternCaptureMode.NonRendered wire token (FS-CRAWL-001 §5.6.1
    /// manual override; hyphenated per the catch-up convention).</summary>
    public const string NonRenderedCapture = "non-rendered";

    /// <summary>
    /// Returns the locked wire name of an enum member: the lowercase CLR name,
    /// except for the frozen hyphenated spec tokens.
    /// </summary>
    /// <typeparam name="TEnum">Enum type.</typeparam>
    /// <param name="value">Enum member.</param>
    /// <returns>The wire string used on the REST surface.</returns>
    public static string ToWireValue<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        if (value is ScheduleTriggerType.CatchUp)
        {
            return CatchUp;
        }

        if (value is WafChallengeClass.ManagedChallenge)
        {
            return ManagedChallenge;
        }

        if (value is WafChallengeClass.RateLimitSoftBlock)
        {
            return RateLimitSoftBlock;
        }

        if (value is CaptureMode.NonJs)
        {
            return NonJsCapture;
        }

        if (value is PatternCaptureMode.NonRendered)
        {
            return NonRenderedCapture;
        }

        return value.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Parses a wire string into an enum member (case-insensitive, mirroring
    /// the Api converter's query-string binding).
    /// </summary>
    /// <typeparam name="TEnum">Enum type.</typeparam>
    /// <param name="text">Wire value.</param>
    /// <param name="value">Parsed member when successful.</param>
    /// <returns>True when the text is a known wire value.</returns>
    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        foreach (TEnum candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(ToWireValue(candidate), text, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}