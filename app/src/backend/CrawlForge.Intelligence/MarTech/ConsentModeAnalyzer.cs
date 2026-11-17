using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// Consent Mode v2 analyzer (FS-CRAWL-002 §14.2/§14.3, locked
/// <see cref="IConsentModeAnalyzer"/>): builds the ordered consent timeline
/// (defaults first, then updates with sequence markers), consumes the
/// keystone consent_state element group, correlates gcs/gcd hit parameters
/// from the proxy capture, and sequences consent-platform load against the
/// first tag fire using CAPTURE TIMESTAMPS. Tags firing before or without a
/// default consent declaration raise analytics.consent-mode-missing.
/// Rendering-off degrades to static signals with an explicit notice.
/// Offline: stored artifacts only, no vendor lookups (E2).
/// </summary>
public sealed class ConsentModeAnalyzer : IConsentModeAnalyzer
{
    private const int MaxEvidenceHits = 10;

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IElementInventoryExtractor _inventoryExtractor;
    private readonly MarTechOptions _options;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="inventoryExtractor">Keystone element inventory extractor (composition).</param>
    /// <param name="options">Local options; null uses enabled defaults.</param>
    public ConsentModeAnalyzer(
        IElementInventoryExtractor inventoryExtractor,
        MarTechOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inventoryExtractor);
        _inventoryExtractor = inventoryExtractor;
        _options = options ?? new MarTechOptions();
    }

    /// <inheritdoc />
    public async Task<ConsentModeAnalysisResult> AnalyzeAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = _options.WithConfiguration(context.Configuration);
        if (!options.Enabled)
        {
            return new ConsentModeAnalysisResult(null, false, []);
        }

        var document = context.RenderedDocument ?? context.Document;
        var commands = ConsentTimelineParser.Parse(document, options.EvidenceExcerptBound);
        var consentModePresent = commands.Count > 0;

        var (platformHints, markers) = await ReadConsentGroupAsync(document, cancellationToken).ConfigureAwait(false);
        var captures = context.CapturedRequests;
        var consentLoads = FindConsentPlatformLoads(captures);
        var analyticsHits = (captures ?? [])
            .Where(request => NetworkHitClassifier.IsAnalyticsHit(request.Url))
            .ToList();

        var findings = new List<FindingDraft>();
        var renderingEnabled = context.Configuration.RenderingMode != RenderMode.None;
        if (!renderingEnabled)
        {
            findings.Add(MarTechFindingFactory.RequiresRenderingNotice(
                context.Url.Id,
                "consent-timeline",
                "rendering mode is disabled for this audit; static consent signals and capture timestamps were used where available."));
        }

        DateTimeOffset? firstHitAt = analyticsHits.Count > 0 ? analyticsHits.Min(hit => hit.CapturedAt) : null;
        DateTimeOffset? consentLoadAt = consentLoads.Count > 0 ? consentLoads.Min(load => load.Request.CapturedAt) : null;
        var defaultDeclared = commands.Any(command => command.Kind == "default");

        if (options.ConsentAuditingEnabled && analyticsHits.Count > 0)
        {
            if (!defaultDeclared)
            {
                findings.Add(BuildConsentFinding(
                    context.Url.Id,
                    "without-default",
                    $"Analytics tags fired without any default consent declaration ({analyticsHits.Count} hit(s) observed).",
                    analyticsHits,
                    firstHitAt,
                    consentLoadAt,
                    platformHints));
            }

            if (firstHitAt is { } firstHit && consentLoadAt is { } consentLoad && firstHit < consentLoad)
            {
                findings.Add(BuildConsentFinding(
                    context.Url.Id,
                    "before-consent-load",
                    $"Analytics tags fired at {firstHit:O} before the consent platform loaded at {consentLoad:O}.",
                    analyticsHits,
                    firstHitAt,
                    consentLoadAt,
                    platformHints));
            }
        }

        var consentStateJson = JsonSerializer.Serialize(new
        {
            present = consentModePresent,
            auditingEnabled = options.ConsentAuditingEnabled,
            defaults = commands.Where(command => command.Kind == "default").Select(ProjectCommand),
            updates = commands.Where(command => command.Kind == "update").Select(ProjectCommand),
            platformHints,
            markers,
            consentPlatformLoads = consentLoads.Select(load => new
            {
                vendor = load.Vendor,
                url = load.Request.Url,
                capturedAt = load.Request.CapturedAt,
            }),
            gcsObservations = analyticsHits
                .Where(hit => NetworkHitClassifier.ExtractGcs(hit.Url) is not null
                    || NetworkHitClassifier.ExtractGcd(hit.Url) is not null)
                .Take(MaxEvidenceHits)
                .Select(hit => ProjectGcsObservation(hit)),
            firstAnalyticsHitAt = firstHitAt,
            consentPlatformLoadAt = consentLoadAt,
            sequencingVerdict = firstHitAt is null || consentLoadAt is null
                ? "insufficient-capture"
                : firstHitAt < consentLoadAt ? "tags-before-consent" : "consent-before-tags",
            catalogueVersion = MarTechCatalogue.Version,
        }, PayloadOptions);

        return new ConsentModeAnalysisResult(consentStateJson, consentModePresent, findings);
    }

    private static object ProjectCommand(ConsentCommand command) => new
    {
        sequence = command.Sequence,
        states = command.States,
        excerpt = command.Excerpt,
    };

    private static object ProjectGcsObservation(ProxyCapturedRequest hit)
    {
        var gcs = NetworkHitClassifier.ExtractGcs(hit.Url);
        var gcd = NetworkHitClassifier.ExtractGcd(hit.Url);
        return new
        {
            url = hit.Url,
            gcs,
            gcd,
            adStorage = DecodeGcsDigit(gcs, 2),
            analyticsStorage = DecodeGcsDigit(gcs, 3),
            capturedAt = hit.CapturedAt,
        };
    }

    /// <summary>Decodes a gcs digit (G1ab): 0 = denied, 1 = granted.</summary>
    private static string? DecodeGcsDigit(string? gcs, int index)
    {
        if (gcs is null || gcs.Length <= index || !gcs.StartsWith("G1", StringComparison.Ordinal))
        {
            return null;
        }

        return gcs[index] switch
        {
            '0' => "denied",
            '1' => "granted",
            _ => null,
        };
    }

    private FindingDraft BuildConsentFinding(
        long urlId,
        string facet,
        string message,
        IReadOnlyList<ProxyCapturedRequest> analyticsHits,
        DateTimeOffset? firstHitAt,
        DateTimeOffset? consentLoadAt,
        IReadOnlyList<string> platformHints)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["facet"] = facet,
            ["hitCount"] = analyticsHits.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["firstAnalyticsHitAt"] = firstHitAt?.ToString("O") ?? string.Empty,
            ["consentPlatformLoadAt"] = consentLoadAt?.ToString("O") ?? string.Empty,
            ["platformHints"] = string.Join(",", platformHints),
        };
        var index = 0;
        foreach (var hit in analyticsHits.Take(MaxEvidenceHits))
        {
            details[$"hit[{index}].url"] = hit.Url;
            details[$"hit[{index}].capturedAt"] = hit.CapturedAt.ToString("O");
            var gcs = NetworkHitClassifier.ExtractGcs(hit.Url);
            if (gcs is not null)
            {
                details[$"hit[{index}].gcs"] = gcs;
            }

            index++;
        }

        return MarTechFindingFactory.Create(
            MarTechRuleKeys.ConsentModeMissing,
            FindingSeverity.Warning,
            urlId,
            new MarTechEvidence(
                MarTechRuleKeys.ConsentModeMissing,
                Vendor: null,
                ContainerId: null,
                message,
                details));
    }

    private async Task<(IReadOnlyList<string> PlatformHints, IReadOnlyList<string> Markers)> ReadConsentGroupAsync(
        DomDocument? document,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return ([], []);
        }

        var inventory = await _inventoryExtractor.ExtractAsync(document, cancellationToken).ConfigureAwait(false);
        var item = inventory.FirstOrDefault(entry => entry.Group == ElementGroups.ConsentState);
        if (item is null)
        {
            return ([], []);
        }

        try
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            var root = payload.RootElement;
            var hints = ReadStringArray(root, "platformHints");
            var markers = ReadStringArray(root, "markers");
            return (hints, markers);
        }
        catch (JsonException)
        {
            return ([], []);
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList()
            : [];

    private static IReadOnlyList<(string Vendor, ProxyCapturedRequest Request)> FindConsentPlatformLoads(
        IReadOnlyList<ProxyCapturedRequest>? captures)
    {
        if (captures is null)
        {
            return [];
        }

        var loads = new List<(string, ProxyCapturedRequest)>();
        foreach (var request in captures)
        {
            foreach (var signature in MarTechCatalogue.Signatures.Where(s => s.Kind == MarTechCatalogue.KindConsent))
            {
                if (signature.NetworkMarkers.Any(marker =>
                    request.Url.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    loads.Add((signature.Vendor, request));
                    break;
                }
            }
        }

        return loads;
    }
}
