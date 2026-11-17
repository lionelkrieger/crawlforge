using System.Text.Json;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Shape of a persisted politeness payload.
/// </summary>
public enum AimdPolitenessShape
{
    /// <summary>No payload was present; the documented defaults were used.</summary>
    Default,

    /// <summary>REST/storage shape: <c>{"aimd":{"initialRateR0":…}}</c>.</summary>
    Nested,

    /// <summary>Legacy flat shape written by first-run seeding and older builds.</summary>
    Flat
}

/// <summary>
/// Result of parsing a persisted politeness payload. Parsing is total: it never
/// throws, and every rejected value is reported so the caller can record a
/// local diagnostic (FS-CRAWL-002 §3.3 defaults, §3.5 observability).
/// </summary>
/// <param name="Settings">Settings safe to hand to <see cref="AimdControllerOptions.FromSettings"/>.</param>
/// <param name="Shape">Which stored shape was recognised.</param>
/// <param name="Fallbacks">Human-readable notes for values that were missing-invalid and defaulted.</param>
public sealed record AimdParseResult(
    AimdSettings Settings,
    AimdPolitenessShape Shape,
    IReadOnlyList<string> Fallbacks);

/// <summary>
/// Parses the persisted <c>PolitenessJson</c> payload into the locked
/// <see cref="AimdSettings"/> DTO. Two shapes exist in user databases:
/// the REST/storage shape (<c>{"aimd":{"initialRateR0":…}}</c>) written by
/// <c>ProjectWireMapper.SerializeAimd</c>, and the legacy flat shape
/// (<c>{"InitialRate":…}</c>) written by first-run seeding. Both are accepted,
/// so existing projects keep working without manual migration.
/// </summary>
public static class AimdSettingsParser
{
    /// <summary>R0 — spec §3.3 default.</summary>
    public const double DefaultInitialRate = 1.0;

    /// <summary>Alpha — spec §3.3 default.</summary>
    public const double DefaultAdditiveIncrease = 0.5;

    /// <summary>Beta — spec §3.3 default.</summary>
    public const double DefaultMultiplicativeDecrease = 0.5;

    /// <summary>Lambda — spec §3.3 EWMA default.</summary>
    public const double DefaultEwmaSmoothing = 0.2;

    /// <summary>R_min — spec §3.3 default.</summary>
    public const double DefaultMinRate = 0.1;

    /// <summary>Per-host concurrency default used by local seeding.</summary>
    public const int DefaultMaxConcurrency = 4;

    /// <summary>
    /// Parses a stored politeness payload. Invalid JSON, wrong value kinds and
    /// out-of-range numbers are replaced by the documented defaults and listed
    /// in <see cref="AimdParseResult.Fallbacks"/>; this method never throws.
    /// </summary>
    /// <param name="json">Raw stored payload (may be null or empty).</param>
    /// <returns>A safe settings DTO and diagnostic notes.</returns>
    public static AimdParseResult Parse(string? json)
    {
        var fallbacks = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AimdParseResult(Defaults(), AimdPolitenessShape.Default, fallbacks);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            fallbacks.Add($"politeness payload is not valid JSON ({ex.Message}); using documented defaults");
            return new AimdParseResult(Defaults(), AimdPolitenessShape.Default, fallbacks);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                fallbacks.Add("politeness payload is not a JSON object; using documented defaults");
                return new AimdParseResult(Defaults(), AimdPolitenessShape.Default, fallbacks);
            }

            var nested = TryGetProperty(root, "aimd", out var aimd) && aimd.ValueKind == JsonValueKind.Object;
            var values = nested ? aimd : root;
            var shape = nested ? AimdPolitenessShape.Nested : AimdPolitenessShape.Flat;

            var initialRate = ReadNumber(values, nested, ["initialRateR0", "InitialRate"],
                DefaultInitialRate, IsPositive, "initial rate (R0)", fallbacks);
            var additive = ReadNumber(values, nested, ["alpha", "AdditiveIncrease"],
                DefaultAdditiveIncrease, IsPositive, "additive increase (alpha)", fallbacks);
            var beta = ReadNumber(values, nested, ["beta", "MultiplicativeDecrease"],
                DefaultMultiplicativeDecrease, IsUnitExclusive, "multiplicative decrease (beta)", fallbacks);
            var ewma = ReadNumber(values, nested, ["ewmaTtfbWeight", "EwmaSmoothing"],
                DefaultEwmaSmoothing, IsUnitInclusive, "EWMA smoothing (lambda)", fallbacks);
            var minRate = ReadNumber(values, nested, ["minRate", "MinRate"],
                DefaultMinRate, IsPositive, "rate floor (R_min)", fallbacks, reportMissing: !nested);
            var maxConcurrency = ReadInt(values, nested, ["maxConcurrency", "MaxConcurrency"],
                DefaultMaxConcurrency, value => value >= 1, "per-host concurrency", fallbacks, reportMissing: !nested);

            var settings = new AimdSettings(initialRate, additive, beta, ewma, minRate, maxConcurrency);
            return new AimdParseResult(settings, shape, fallbacks);
        }
    }

    private static AimdSettings Defaults() => new(
        DefaultInitialRate,
        DefaultAdditiveIncrease,
        DefaultMultiplicativeDecrease,
        DefaultEwmaSmoothing,
        DefaultMinRate,
        DefaultMaxConcurrency);

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static double ReadNumber(
        JsonElement obj,
        bool nested,
        IReadOnlyList<string> names,
        double fallback,
        Func<double, bool> valid,
        string label,
        List<string> fallbacks,
        bool reportMissing = true)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(obj, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && valid(number))
            {
                return number;
            }

            fallbacks.Add($"{label} is invalid; using {fallback}");
            return fallback;
        }

        if (reportMissing && !nested)
        {
            fallbacks.Add($"{label} is missing; using {fallback}");
        }

        return fallback;
    }

    private static int ReadInt(
        JsonElement obj,
        bool nested,
        IReadOnlyList<string> names,
        int fallback,
        Func<int, bool> valid,
        string label,
        List<string> fallbacks,
        bool reportMissing = true)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(obj, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && valid(number))
            {
                return number;
            }

            fallbacks.Add($"{label} is invalid; using {fallback}");
            return fallback;
        }

        if (reportMissing && !nested)
        {
            fallbacks.Add($"{label} is missing; using {fallback}");
        }

        return fallback;
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static bool IsUnitExclusive(double value) => double.IsFinite(value) && value is > 0 and < 1;

    private static bool IsUnitInclusive(double value) => double.IsFinite(value) && value is > 0 and <= 1;
}
