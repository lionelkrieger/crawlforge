using System.Text.Json;
using CrawlForge.Contracts.Entities;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// One user-defined beacon pattern (FS-CRAWL-002 §14.1): a local regular
/// expression over script sources, inline markers, or network hit URLs.
/// Patterns are time-bounded (§7.16 discipline) and never fetched remotely.
/// </summary>
/// <param name="Name">User-facing beacon label.</param>
/// <param name="Pattern">Regular expression.</param>
/// <param name="Target">Match target: script-src | inline | network.</param>
public sealed record CustomBeaconPattern(string Name, string Pattern, string Target);

/// <summary>
/// Local MarTech analyzer options (FS-CRAWL-002 §14). Enabled by default
/// within bounds (criterion #47); every toggle is local-only. Configuration
/// additive fields (consent toggle, primary override, dataLayer schema,
/// beacon patterns) arrive through the audit configuration snapshot.
/// </summary>
public sealed class MarTechOptions
{
    /// <summary>Category enablement (default true).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Consent Mode auditing enablement (default true).</summary>
    public bool ConsentAuditingEnabled { get; init; } = true;

    /// <summary>
    /// Primary container override (container ID, e.g. GTM-ABC123 or G-XYZ).
    /// When null, the primary may be supplied from audit-wide aggregation
    /// via the auditor constructor.
    /// </summary>
    public string? PrimaryContainerOverride { get; init; }

    /// <summary>Optional user-supplied dataLayer JSON schema (bounded validation).</summary>
    public string? DataLayerSchemaJson { get; init; }

    /// <summary>User-defined local beacon patterns.</summary>
    public IReadOnlyList<CustomBeaconPattern> CustomBeacons { get; init; } = [];

    /// <summary>Maximum dataLayer events captured per page (spec default 100).</summary>
    public int DataLayerEventBound { get; init; } = 100;

    /// <summary>Per-pattern regex timeout, milliseconds (§7.16 time bounds).</summary>
    public int PatternTimeoutMs { get; init; } = 250;

    /// <summary>Maximum evidence excerpt length, characters.</summary>
    public int EvidenceExcerptBound { get; init; } = 512;

    /// <summary>
    /// Builds options from the per-category audit toggles payload. Missing or
    /// malformed payloads leave the category enabled (baseline-preserving).
    /// Recognized keys: analytics / martech / analytics-tagging (case-insensitive).
    /// </summary>
    /// <param name="auditTogglesJson">Application settings toggles payload.</param>
    /// <returns>Options with the resolved enablement.</returns>
    public static MarTechOptions FromAuditTogglesJson(string? auditTogglesJson)
    {
        if (string.IsNullOrWhiteSpace(auditTogglesJson))
        {
            return new MarTechOptions();
        }

        try
        {
            using var document = JsonDocument.Parse(auditTogglesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MarTechOptions();
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if ((property.Name.Equals("analytics", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("martech", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("analytics-tagging", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return new MarTechOptions { Enabled = property.Value.GetBoolean() };
                }
            }
        }
        catch (JsonException)
        {
            // Malformed local payload: baseline default (enabled).
        }

        return new MarTechOptions();
    }

    /// <summary>
    /// Builds options from an audit configuration snapshot: category toggles
    /// plus the §14.4 additive fields (consent toggle, primary override,
    /// dataLayer schema, custom beacon patterns).
    /// </summary>
    /// <param name="configuration">Configuration snapshot.</param>
    /// <param name="auditTogglesJson">Optional category toggles payload.</param>
    /// <returns>Resolved options.</returns>
    public static MarTechOptions FromConfiguration(
        CrawlConfigurationDto configuration,
        string? auditTogglesJson = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var baseline = FromAuditTogglesJson(auditTogglesJson);
        return new MarTechOptions
        {
            Enabled = baseline.Enabled,
            ConsentAuditingEnabled = configuration.ConsentModeEnabled ?? true,
            PrimaryContainerOverride = configuration.PrimaryContainerOverride,
            DataLayerSchemaJson = configuration.DataLayerSchemaJson,
            CustomBeacons = ParseBeacons(configuration.BeaconPatternsJson),
            DataLayerEventBound = baseline.DataLayerEventBound,
            PatternTimeoutMs = baseline.PatternTimeoutMs,
            EvidenceExcerptBound = baseline.EvidenceExcerptBound,
        };
    }

    /// <summary>
    /// Resolves effective options against an audit configuration snapshot:
    /// the §14.4 additive fields (consent toggle, primary override,
    /// dataLayer schema, beacon patterns) override the constructor baseline
    /// when present. Category enablement stays with the baseline toggles.
    /// </summary>
    /// <param name="configuration">Configuration snapshot.</param>
    /// <returns>Effective options for one page audit.</returns>
    public MarTechOptions WithConfiguration(CrawlConfigurationDto configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredBeacons = ParseBeacons(configuration.BeaconPatternsJson);
        return new MarTechOptions
        {
            Enabled = Enabled,
            ConsentAuditingEnabled = configuration.ConsentModeEnabled ?? ConsentAuditingEnabled,
            PrimaryContainerOverride = configuration.PrimaryContainerOverride ?? PrimaryContainerOverride,
            DataLayerSchemaJson = configuration.DataLayerSchemaJson ?? DataLayerSchemaJson,
            CustomBeacons = configuredBeacons.Count > 0 ? configuredBeacons : CustomBeacons,
            DataLayerEventBound = DataLayerEventBound,
            PatternTimeoutMs = PatternTimeoutMs,
            EvidenceExcerptBound = EvidenceExcerptBound,
        };
    }

    private static IReadOnlyList<CustomBeaconPattern> ParseBeacons(string? beaconPatternsJson)
    {
        if (string.IsNullOrWhiteSpace(beaconPatternsJson))
        {
            return [];
        }

        try
        {
            var patterns = JsonSerializer.Deserialize<List<CustomBeaconPattern>>(
                beaconPatternsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return patterns is null
                ? []
                : patterns.Where(pattern =>
                    !string.IsNullOrWhiteSpace(pattern.Name)
                    && !string.IsNullOrWhiteSpace(pattern.Pattern)
                    && !string.IsNullOrWhiteSpace(pattern.Target)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
