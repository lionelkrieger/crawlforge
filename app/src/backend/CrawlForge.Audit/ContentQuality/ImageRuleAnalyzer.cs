using System.Globalization;
using System.Text.Json;
using CrawlForge.Audit.Accessibility;
using CrawlForge.Audit.Accessibility.Rules;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Stable rule keys of the image rule family (FS-CRAWL-001 §7.4 Images row:
/// "Missing or empty alternative text; broken sources; oversized payloads;
/// missing dimensions"; CAP-B-056 oversized images, CAP-B-057 format checks).
/// D-021 (phase 10): the locked <see cref="AuditCategory.Images"/> member
/// (WP-10-A1) is the category for every finding of this family — payload,
/// format AND alternative text. The alternative-text check is produced here
/// (coordinator-approved takeover of <c>ImageAltRule</c>; the production
/// AccessibilityAnalyzer disables the same key through RuleOverrides so the
/// finding is emitted exactly once, under Images, with the identical key,
/// semantics and evidence shape).
/// </summary>
public static class ImageRuleKeys
{
    /// <summary>An image's transferred payload exceeds the configured byte
    /// threshold. Locked catalog key (WP-10-A1 FindingRuleKeys).</summary>
    public const string Oversized = FindingRuleKeys.ImageOversized;

    /// <summary>An image uses a non-web or legacy format (format check).
    /// Locked catalog key (WP-10-A1 FindingRuleKeys).</summary>
    public const string Format = FindingRuleKeys.ImageFormat;

    /// <summary>
    /// Missing alternative text (D-021 takeover). The EXACT locked accessibility
    /// key string — the phase gate filters the Images category for this key;
    /// it is never re-spelled or renamed.
    /// </summary>
    public const string ImageAlt = AccessibilityRuleKeys.ImageAlt;
}

/// <summary>
/// One image resource fact evaluated by <see cref="ImageRuleAnalyzer"/>.
/// Transferred bytes are optional (CR-031): observable for inline data-URI
/// images offline, and for network images when a capture source supplies them
/// (rendered/full-capture NetworkWaterfall artifact). Unknown size is reported
/// as "not measured" — never as a pass.
/// </summary>
/// <param name="Src">Image source reference as declared (absolute/relative URL or data URI).</param>
/// <param name="TransferredBytes">Payload size in bytes, when known.</param>
/// <param name="DeclaredWidth">Declared width attribute, when present.</param>
/// <param name="DeclaredHeight">Declared height attribute, when present.</param>
/// <param name="BytesSource">Additive (CR-031): where the byte measurement came
/// from ("data-uri", "network-waterfall", or a composition-supplied label);
/// null when the size is not measured.</param>
public sealed record ImageFact(
    string Src,
    long? TransferredBytes = null,
    int? DeclaredWidth = null,
    int? DeclaredHeight = null,
    string? BytesSource = null);

/// <summary>
/// Image-rule configuration (FS-CRAWL-001 §7.5: locally configurable
/// thresholds with application defaults; E2: no remote catalogue).
/// </summary>
/// <param name="OversizedBytesThreshold">
/// Transferred payload above which an image is oversized. Application default
/// 200,000 bytes (200 KB) — a common page-weight budget for a single image.
/// </param>
/// <param name="RecommendModernFormats">
/// Whether legacy-but-valid raster formats (JPEG/PNG/GIF) earn a Notice
/// recommending modern formats (WebP/AVIF). Non-web formats always warn.
/// </param>
public sealed record ImageRuleOptions(
    long OversizedBytesThreshold = 200_000,
    bool RecommendModernFormats = true)
{
    /// <summary>Default options.</summary>
    public static ImageRuleOptions Default { get; } = new();
}

/// <summary>
/// Image rules (FS-CRAWL-001 §7.4 Images row as amended by D-021/CR-031;
/// CAP-B-056 oversized images, CAP-B-057 format checks). Per-URL analyzer on
/// the generic rule-host surface (<see cref="IAuditRuleAnalyzer"/>):
/// alternative text (the <c>accessibility.image-alt</c> takeover, emitted under
/// <see cref="AuditCategory.Images"/>), oversized payloads (transferred bytes
/// where the capture records them, otherwise an explicit "not measured"
/// report — never a silent pass) and format checks (fully offline from the
/// source reference).
/// </summary>
/// <remarks>
/// <para>
/// Pure and offline: a function of the stored document's <c>img</c> inventory
/// (parity with the pipeline's <c>images</c> element group) and, when injected,
/// the stored NetworkWaterfall content artifact of the same URL — no image
/// fetches (network boundary §2.6: subresources are not crawl targets).
/// Re-audits recompute identically from stored artifacts (§9.6). Thread-safe:
/// no mutable instance state. One finding per unique source per rule (repeated
/// <c>img</c> elements referencing the same source are reported once).
/// </para>
/// <para>
/// CR-031 measurement sources: inline base64 data-URI payloads are decoded
/// locally; network images read <see cref="RenderedNetworkEntry.TransferredBytes"/>
/// from the URL's <see cref="ContentArtifactKind.NetworkWaterfall"/> artifact
/// (rendered/full capture; 0 means unknown). Plain HTTP capture records no
/// subresource sizes, so those images are reported as not measured (Notice)
/// rather than passing. The locked <see cref="ProxyCapturedRequest"/> carries
/// no byte field, so http-mode proxy captures cannot supply sizes either
/// (recorded contract gap — phase-10 handoff).
/// </para>
/// <para>
/// Format vocabulary: non-web formats (BMP, TIFF, PSD, camera RAW, HEIC/HEIF)
/// warn — browsers cannot render them reliably; legacy raster formats
/// (JPEG/PNG/GIF) earn a Notice recommending WebP/AVIF when
/// <see cref="ImageRuleOptions.RecommendModernFormats"/> is on; modern formats
/// (WebP, AVIF, JPEG XL, SVG, ICO) pass silently. Unknown extensions produce
/// no finding (nothing verifiable offline).
/// </para>
/// <para>
/// Alternative text (D-021): the SAME <c>ImageAltRule</c> implementation the
/// accessibility family uses evaluates the effective document (rendered tree
/// when available, CR-009), preserving every exemption (decorative
/// role=presentation/none, aria-hidden=true, alt present including empty,
/// figure/figcaption text alternative, input[type=image], area). Violations
/// materialize with the identical <see cref="AccessibilityEvidence"/> shape
/// and the bundled Error severity, under <see cref="AuditCategory.Images"/>.
/// The coordinator disables the production AccessibilityAnalyzer's copy of
/// the key (RuleOverrides) so exactly one producer emits it.
/// </para>
/// </remarks>
public sealed class ImageRuleAnalyzer : IAuditRuleAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions WaterfallJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> NonWebFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "bmp", "tiff", "tif", "psd", "raw", "cr2", "nef", "arw", "dng", "heic", "heif",
    };

    private static readonly HashSet<string> LegacyRasterFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpeg", "jpg", "png", "gif",
    };

    private readonly ImageRuleOptions _options;
    private readonly IReadRepository<ContentArtifactDto>? _artifacts;
    private readonly IContentStore? _contentStore;

    /// <summary>Creates the analyzer with local configuration.</summary>
    /// <param name="options">Image-rule options; null uses bundled defaults.</param>
    /// <param name="artifacts">
    /// Additive (CR-031): content-artifact read repository used to locate the
    /// URL's NetworkWaterfall artifact; null disables artifact-based byte
    /// measurement (data-URI measurement still applies). DI handoff:
    /// EngineServicesModule supplies the registered repositories.
    /// </param>
    /// <param name="contentStore">
    /// Additive (CR-031): content store used to read waterfall payloads; null
    /// disables artifact-based byte measurement.
    /// </param>
    public ImageRuleAnalyzer(
        ImageRuleOptions? options = null,
        IReadRepository<ContentArtifactDto>? artifacts = null,
        IContentStore? contentStore = null)
    {
        _options = options ?? ImageRuleOptions.Default;
        _artifacts = artifacts;
        _contentStore = contentStore;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.OversizedBytesThreshold, 1);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys => [ImageRuleKeys.Oversized, ImageRuleKeys.Format, ImageRuleKeys.ImageAlt];

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Parity with the pipeline's images element group: the response
        // document's img inventory (rendered-only images are the §7.18
        // response-vs-rendered comparer's domain).
        var document = context.Document;
        if (document is null)
        {
            return [];
        }

        var facts = new List<ImageFact>();
        foreach (var image in EnumerateImages(document.Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = Attribute(image, "src");
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            var trimmed = src.Trim();
            var dataUriBytes = DataUriPayloadBytes(trimmed);
            facts.Add(new ImageFact(
                Src: trimmed,
                TransferredBytes: dataUriBytes,
                DeclaredWidth: ParseDimension(Attribute(image, "width")),
                DeclaredHeight: ParseDimension(Attribute(image, "height")),
                BytesSource: dataUriBytes is not null ? "data-uri" : null));
        }

        // CR-031: transferred bytes from the stored NetworkWaterfall artifact
        // (rendered/full capture). Never fetched at runtime — the artifact was
        // persisted by the capture phase of THIS audit.
        var waterfallBytes = await LoadWaterfallBytesAsync(context.AuditId, context.Url.Id, cancellationToken).ConfigureAwait(false);
        if (waterfallBytes.Count > 0)
        {
            for (var i = 0; i < facts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (facts[i].TransferredBytes is not null)
                {
                    continue;
                }

                var absolute = ResolveSrc(facts[i].Src, context.Url.CanonicalUrl);
                if (absolute is not null && waterfallBytes.TryGetValue(absolute, out var bytes))
                {
                    facts[i] = facts[i] with { TransferredBytes = bytes, BytesSource = "network-waterfall" };
                }
            }
        }

        var findings = new List<FindingDraft>(EvaluateImages(context.Url.Id, facts));

        // D-021 alt-check takeover: the same ImageAltRule over the effective
        // document (rendered tree when available), materialized under Images.
        var effectiveDocument = context.RenderedDocument ?? document;
        var ruleContext = new AccessibilityRuleContext(
            effectiveDocument,
            renderingEnabled: context.Configuration.RenderingMode != RenderMode.None,
            renderedTreeAvailable: context.RenderedDocument is not null,
            new AccessibilityAnalyzerOptions());
        ImageAltRule.Evaluate(ruleContext);
        foreach (var violation in ruleContext.Violations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(MaterializeAltViolation(violation, context.Url.Id));
        }

        return findings;
    }

    /// <summary>
    /// Pure evaluation core: applies the oversized and format rules to explicit
    /// image facts. Composition sources that observe transferred bytes
    /// (rendered resource timing, future subresource capture) feed this method
    /// directly; the context adapter supplies DOM-declared facts plus stored
    /// waterfall bytes. CR-031: an image whose size is unknown earns an
    /// explicit "not measured" Notice — never a silent pass.
    /// </summary>
    /// <param name="urlId">Owning URL record identifier.</param>
    /// <param name="images">Image facts in document order.</param>
    /// <returns>Findings, one per unique source per rule.</returns>
    public IReadOnlyList<FindingDraft> EvaluateImages(long urlId, IEnumerable<ImageFact> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        var findings = new List<FindingDraft>();
        var oversizedReported = new HashSet<string>(StringComparer.Ordinal);
        var formatReported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.Src))
            {
                continue;
            }

            var src = image.Src.Trim();

            if (oversizedReported.Add(src))
            {
                if (image.TransferredBytes is { } bytes)
                {
                    if (bytes > _options.OversizedBytesThreshold)
                    {
                        findings.Add(Finding(
                            ImageRuleKeys.Oversized,
                            FindingSeverity.Warning,
                            urlId,
                            new
                            {
                                src,
                                measurement = "measured",
                                bytesSource = image.BytesSource ?? "capture",
                                bytes,
                                threshold = _options.OversizedBytesThreshold,
                                declaredWidth = image.DeclaredWidth,
                                declaredHeight = image.DeclaredHeight,
                            }));
                    }

                    // Measured and within budget: a genuine pass, no finding.
                }
                else
                {
                    // CR-031: unknown size is reported, never passed.
                    findings.Add(Finding(
                        ImageRuleKeys.Oversized,
                        FindingSeverity.Notice,
                        urlId,
                        new
                        {
                            src,
                            measurement = "not-measured",
                            threshold = _options.OversizedBytesThreshold,
                            declaredWidth = image.DeclaredWidth,
                            declaredHeight = image.DeclaredHeight,
                            reason = src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                                ? "The inline data-URI payload is percent-encoded and was not decoded; its size is not measured."
                                : "The capture mode records no transferred bytes for this image (CR-031); a rendered or full capture measures it.",
                        }));
                }
            }

            var format = DetectFormat(src);
            if (format is null || !formatReported.Add(src))
            {
                continue;
            }

            if (NonWebFormats.Contains(format))
            {
                findings.Add(Finding(
                    ImageRuleKeys.Format,
                    FindingSeverity.Warning,
                    urlId,
                    new
                    {
                        src,
                        format,
                        issue = "non-web-format",
                        recommendation = "Convert to a web-native format (WebP, AVIF, PNG or JPEG); browsers cannot render this format reliably.",
                    }));
            }
            else if (_options.RecommendModernFormats && LegacyRasterFormats.Contains(format))
            {
                findings.Add(Finding(
                    ImageRuleKeys.Format,
                    FindingSeverity.Notice,
                    urlId,
                    new
                    {
                        src,
                        format,
                        issue = "legacy-format",
                        recommendation = "Consider a modern format (WebP or AVIF) for smaller payloads at equivalent quality.",
                    }));
            }
        }

        return findings;
    }

    /// <summary>
    /// Detects the image format from a source reference: data-URI media type
    /// first, then the URL path extension (query/fragment stripped). Null when
    /// nothing verifiable is declared.
    /// </summary>
    /// <param name="src">Image source reference.</param>
    /// <returns>Lowercase format token, or null.</returns>
    public static string? DetectFormat(string src)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(src);

        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = src.IndexOf(',');
            if (comma < 0)
            {
                return null;
            }

            var meta = src[5..comma];
            var semicolon = meta.IndexOf(';');
            var mediaType = (semicolon < 0 ? meta : meta[..semicolon]).Trim();
            const string imagePrefix = "image/";
            return mediaType.StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase)
                ? NormalizeFormatToken(mediaType[imagePrefix.Length..])
                : null;
        }

        var path = src;
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path[..query];
        }

        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
        {
            return null;
        }

        var extension = path[(dot + 1)..];
        // Extensions may contain digits (cr2, jxl); non-alphanumeric suffixes
        // are not format tokens.
        return extension.Length > 0 && extension.All(char.IsLetterOrDigit)
            ? NormalizeFormatToken(extension)
            : null;
    }

    /// <summary>
    /// Reads the URL's stored NetworkWaterfall artifacts and maps request URL
    /// → transferred bytes (only positive measurements; 0 means unknown per
    /// the locked contract). Accepts a JSON array of
    /// <see cref="RenderedNetworkEntry"/> or an object wrapping one under
    /// "networkWaterfall"/"entries" (case-insensitive). Missing artifacts,
    /// unreadable payloads and absent repositories yield an empty map — the
    /// oversized check then reports "not measured" (CR-031).
    /// </summary>
    private async Task<Dictionary<string, long>> LoadWaterfallBytesAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        var bytesByUrl = new Dictionary<string, long>(StringComparer.Ordinal);
        if (_artifacts is null || _contentStore is null)
        {
            return bytesByUrl;
        }

        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new ListQuery(
                Offset: offset,
                Limit: 50,
                Filters: new Dictionary<string, string>
                {
                    ["auditId"] = auditId.ToString(CultureInfo.InvariantCulture),
                    ["urlId"] = urlId.ToString(CultureInfo.InvariantCulture),
                    ["kind"] = ((int)ContentArtifactKind.NetworkWaterfall).ToString(CultureInfo.InvariantCulture),
                });
            var page = await _artifacts.ListAsync(query, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var artifact in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = await _contentStore.OpenReadAsync(artifact.StorageKey, cancellationToken).ConfigureAwait(false);
                if (stream is null)
                {
                    continue;
                }

                foreach (var entry in await ReadWaterfallEntriesAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    if (entry.TransferredBytes > 0 && !bytesByUrl.ContainsKey(entry.Url))
                    {
                        bytesByUrl[entry.Url] = entry.TransferredBytes;
                    }
                }
            }

            offset += page.Items.Count;
            if (offset >= page.TotalCount)
            {
                break;
            }
        }

        return bytesByUrl;
    }

    private static async Task<IReadOnlyList<RenderedNetworkEntry>> ReadWaterfallEntriesAsync(Stream stream, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var wrapper in new[] { "networkWaterfall", "entries" })
                {
                    if (root.TryGetProperty(wrapper, out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
                    {
                        root = wrapped;
                        break;
                    }
                }
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            try
            {
                return root.Deserialize<List<RenderedNetworkEntry>>(WaterfallJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private static string? ResolveSrc(string src, string pageUrl) =>
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)
        && Uri.TryCreate(baseUri, src, out var resolved)
            ? resolved.AbsoluteUri
            : null;

    private static FindingDraft MaterializeAltViolation(AccessibilityViolation violation, long urlId)
    {
        var descriptor = AccessibilityRuleSet.Find(violation.RuleKey)
            ?? throw new InvalidOperationException($"Violation references unknown rule '{violation.RuleKey}'.");

        // Identical evidence shape to AccessibilityAnalyzer.MaterializeFinding
        // (camelCase AccessibilityEvidence); image-alt is not render-dependent.
        var evidence = new AccessibilityEvidence(
            violation.RuleKey,
            AccessibilityRuleSet.Version,
            descriptor.Family,
            violation.Locator,
            violation.XPath,
            violation.ElementSnippet,
            violation.Message,
            descriptor.Remediation,
            RequiresRendering: false,
            violation.Details);

        return new FindingDraft(
            violation.RuleKey,
            AuditCategory.Images,
            descriptor.DefaultSeverity,
            urlId,
            JsonSerializer.Serialize(evidence, JsonOptions),
            RecommendationKey: violation.RuleKey);
    }

    private static string NormalizeFormatToken(string token)
    {
        var lower = token.ToLowerInvariant();
        return lower switch
        {
            "jpg" => "jpeg",
            "tif" => "tiff",
            "jxl" => "jxl",
            _ => lower,
        };
    }

    private static long? DataUriPayloadBytes(string src)
    {
        if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = src.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var meta = src[5..comma];
        if (!meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            // Percent-encoded data URIs: size is not decoded here (bounded
            // evidence only); the format check still applies and the oversized
            // check reports "not measured" (CR-031).
            return null;
        }

        var payload = src[(comma + 1)..];
        var padding = payload.EndsWith("==", StringComparison.Ordinal) ? 2
            : payload.EndsWith('=') ? 1
            : 0;
        var bytes = (long)payload.Length * 3 / 4 - padding;
        return bytes >= 0 ? bytes : null;
    }

    private static IEnumerable<DomNode> EnumerateImages(DomNode node)
    {
        if (string.Equals(node.TagName, "img", StringComparison.OrdinalIgnoreCase))
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var image in EnumerateImages(child))
            {
                yield return image;
            }
        }
    }

    private static string? Attribute(DomNode node, string name)
    {
        foreach (var attribute in node.Attributes)
        {
            if (string.Equals(attribute.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static int? ParseDimension(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
            ? parsed
            : null;

    private static FindingDraft Finding(string ruleKey, FindingSeverity severity, long urlId, object evidence) =>
        new(ruleKey, AuditCategory.Images, severity, urlId,
            JsonSerializer.Serialize(evidence, JsonOptions), RecommendationKeys.ForRule(ruleKey));
}
