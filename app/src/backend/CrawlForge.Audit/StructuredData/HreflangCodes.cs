namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Offline ISO language/region code validation for hreflang and html lang
/// annotations (FS-CRAWL-001 §7.13). Bundled ISO 639-1 language codes and
/// ISO 3166-1 alpha-2 region codes; no runtime fetch (exclusions E2/E4).
/// Accepts <c>lang</c>, <c>lang-REGION</c>, <c>lang-Script</c>, and
/// <c>lang-Script-REGION</c> shapes plus the special <c>x-default</c> value.
/// </summary>
public static class HreflangCodes
{
    /// <summary>The special hreflang value for language-independent fallback pages.</summary>
    public const string XDefault = "x-default";

    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "aa", "ab", "ae", "af", "ak", "am", "an", "ar", "as", "av", "ay", "az",
        "ba", "be", "bg", "bh", "bi", "bm", "bn", "bo", "br", "bs",
        "ca", "ce", "ch", "co", "cr", "cs", "cu", "cv", "cy",
        "da", "de", "dv", "dz",
        "ee", "el", "en", "eo", "es", "et", "eu",
        "fa", "ff", "fi", "fj", "fo", "fr", "fy",
        "ga", "gd", "gl", "gn", "gu", "gv",
        "ha", "he", "hi", "ho", "hr", "ht", "hu", "hy", "hz",
        "ia", "id", "ie", "ig", "ii", "ik", "io", "is", "it", "iu",
        "ja", "jv",
        "ka", "kg", "ki", "kj", "kk", "kl", "km", "kn", "ko", "kr", "ks", "ku", "kv", "kw", "ky",
        "la", "lb", "lg", "li", "ln", "lo", "lt", "lu", "lv",
        "mg", "mh", "mi", "mk", "ml", "mn", "mr", "ms", "mt", "my",
        "na", "nb", "nd", "ne", "ng", "nl", "nn", "no", "nr", "nv", "ny",
        "oc", "oj", "om", "or", "os",
        "pa", "pi", "pl", "ps", "pt",
        "qu",
        "rm", "rn", "ro", "ru", "rw",
        "sa", "sc", "sd", "se", "sg", "si", "sk", "sl", "sm", "sn", "so", "sq", "sr", "ss", "st", "su", "sv", "sw",
        "ta", "te", "tg", "th", "ti", "tk", "tl", "tn", "to", "tr", "ts", "tt", "tw", "ty",
        "ug", "uk", "ur", "uz",
        "ve", "vi", "vo",
        "wa", "wo",
        "xh",
        "yi", "yo",
        "za", "zh", "zu",
    };

    private static readonly HashSet<string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ad", "ae", "af", "ag", "ai", "al", "am", "ao", "aq", "ar", "as", "at", "au", "aw", "ax", "az",
        "ba", "bb", "bd", "be", "bf", "bg", "bh", "bi", "bj", "bl", "bm", "bn", "bo", "bq", "br", "bs", "bt", "bv", "bw", "by", "bz",
        "ca", "cc", "cd", "cf", "cg", "ch", "ci", "ck", "cl", "cm", "cn", "co", "cr", "cu", "cv", "cw", "cx", "cy", "cz",
        "de", "dj", "dk", "dm", "do", "dz",
        "ec", "ee", "eg", "eh", "er", "es", "et",
        "fi", "fj", "fk", "fm", "fo", "fr",
        "ga", "gb", "gd", "ge", "gf", "gg", "gh", "gi", "gl", "gm", "gn", "gp", "gq", "gr", "gs", "gt", "gu", "gw", "gy",
        "hk", "hm", "hn", "hr", "ht", "hu",
        "id", "ie", "il", "im", "in", "io", "iq", "ir", "is", "it",
        "je", "jm", "jo", "jp",
        "ke", "kg", "kh", "ki", "km", "kn", "kp", "kr", "kw", "ky", "kz",
        "la", "lb", "lc", "li", "lk", "lr", "ls", "lt", "lu", "lv", "ly",
        "ma", "mc", "md", "me", "mf", "mg", "mh", "mk", "ml", "mm", "mn", "mo", "mp", "mq", "mr", "ms", "mt", "mu", "mv", "mw", "mx", "my", "mz",
        "na", "nc", "ne", "nf", "ng", "ni", "nl", "no", "np", "nr", "nu", "nz",
        "om",
        "pa", "pe", "pf", "pg", "ph", "pk", "pl", "pm", "pn", "pr", "ps", "pt", "pw", "py",
        "qa",
        "re", "ro", "rs", "ru", "rw",
        "sa", "sb", "sc", "sd", "se", "sg", "sh", "si", "sj", "sk", "sl", "sm", "sn", "so", "sr", "ss", "st", "sv", "sx", "sy", "sz",
        "tc", "td", "tf", "tg", "th", "tj", "tk", "tl", "tm", "tn", "to", "tr", "tt", "tv", "tw", "tz",
        "ua", "ug", "um", "us", "uy", "uz",
        "va", "vc", "ve", "vg", "vi", "vn", "vu",
        "wf", "ws",
        "ye", "yt",
        "za", "zm", "zw",
    };

    /// <summary>Whether the value is a valid hreflang annotation value.</summary>
    /// <param name="value">Raw hreflang value.</param>
    /// <returns>True for valid language tags and x-default.</returns>
    public static bool IsValidHreflang(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, XDefault, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsValidLanguageTag(trimmed, allowRegion: true);
    }

    /// <summary>Whether the value is a valid html lang language tag.</summary>
    /// <param name="value">Raw lang attribute value.</param>
    /// <returns>True for valid language tags (region optional).</returns>
    public static bool IsValidLanguageTag(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IsValidLanguageTag(value.Trim(), allowRegion: true);

    private static bool IsValidLanguageTag(string value, bool allowRegion)
    {
        var parts = value.Split('-');
        if (parts.Length is < 1 or > 3 || !Languages.Contains(parts[0]))
        {
            return false;
        }

        var index = 1;
        if (parts.Length > index && parts[index].Length == 4)
        {
            // Optional ISO 15924 script subtag (four letters, e.g. zh-Hant).
            if (!parts[index].All(char.IsLetter))
            {
                return false;
            }

            index++;
        }

        if (parts.Length > index)
        {
            return allowRegion && parts[index].Length == 2 && Regions.Contains(parts[index]) && index == parts.Length - 1;
        }

        return index == parts.Length;
    }
}
