using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Stable rule keys of the image rule family (FS-CRAWL-001 §7.4 Images row:
/// "oversized payloads"; CAP-B-056 oversized images, CAP-B-057 format checks).
/// Category mapping note (WP-09-05 handoff): the spec's abstract "Images"
/// category has no member in the locked <see cref="AuditCategory"/> enum;
/// payload/format heuristics map to <see cref="AuditCategory.Performance"/>
/// (page-weight/resource heuristics), while alternative-text rules remain in
/// the Accessibility family (<c>ImageAltRule</c>).
/// </summary>
public static class ImageRuleKeys
{
    /// <summary>An image's transferred payload exceeds the configured byte threshold.</summary>
    public const string Oversized = "image.oversized";

    /// <summary>An image uses a non-web or legacy format (format check).</summary>
    public const string Format = "image.format";
}

/// <summary>
/// One image resource fact evaluated by <see cref="ImageRuleAnalyzer"/>.
/// Transferred bytes are optional: they are observable for inline data-URI
/// images offline, and for network images only when a capture source supplies
/// them (rendered resource timing — rendering phase; the current HTTP fetch
/// path does not record subresource sizes).
/// </summary>
/// <param name="Src">Image source reference as declared (absolute/relative URL or data URI).</param>
/// <param name="TransferredBytes">Payload size in bytes, when known.</param>
/// <param name="DeclaredWidth">Declared width attribute, when present.</param>
/// <param name="DeclaredHeight">Declared height attribute, when present.</param>
public sealed record ImageFact(
    string Src,
    long? TransferredBytes = null,
    int? DeclaredWidth = null,
    int? DeclaredHeight = null);

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
/// Image rules (FS-CRAWL-001 §7.4 Images row; CAP-B-056 oversized images,
/// CAP-B-057 format checks). Per-URL analyzer on the generic rule-host surface
/// (<see cref="IAuditRuleAnalyzer"/>): oversized payloads (when the byte size
/// is observable) and format checks (fully offline from the source reference).
/// </summary>
/// <remarks>
/// <para>
/// Pure and offline: a function of the stored document's <c>img</c> inventory
/// (parity with the pipeline's <c>images</c> element group) — no image fetches
/// (network boundary §2.6: subresources are not crawl targets). Re-audits
/// recompute identically from stored artifacts (§9.6). Thread-safe: no
/// mutable instance state. One finding per unique source per rule (repeated
/// <c>img</c> elements referencing the same source are reported once).
/// </para>
/// <para>
/// Format vocabulary: non-web formats (BMP, TIFF, PSD, camera RAW, HEIC/HEIF)
/// warn — browsers cannot render them reliably; legacy raster formats
/// (JPEG/PNG/GIF) earn a Notice recommending WebP/AVIF when
/// <see cref="ImageRuleOptions.RecommendModernFormats"/> is on; modern formats
/// (WebP, AVIF, JPEG XL, SVG, ICO) pass silently. Unknown extensions produce
/// no finding (nothing verifiable offline). Data-URI images contribute their
/// decoded payload size, so oversized inline images are detectable today.
/// </para>
/// </remarks>
public sealed class ImageRuleAnalyzer : IAuditRuleAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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

    /// <summary>Creates the analyzer with local configuration.</summary>
    /// <param name="options">Image-rule options; null uses bundled defaults.</param>
    public ImageRuleAnalyzer(ImageRuleOptions? options = null)
    {
        _options = options ?? ImageRuleOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.OversizedBytesThreshold, 1);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys => [ImageRuleKeys.Oversized, ImageRuleKeys.Format];

    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Parity with the pipeline's images element group: the response
        // document's img inventory (rendered-only images are the §7.18
        // response-vs-rendered comparer's domain).
        var document = context.Document;
        if (document is null)
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
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

            facts.Add(new ImageFact(
                Src: src.Trim(),
                TransferredBytes: DataUriPayloadBytes(src.Trim()),
                DeclaredWidth: ParseDimension(Attribute(image, "width")),
                DeclaredHeight: ParseDimension(Attribute(image, "height"))));
        }

        return Task.FromResult(EvaluateImages(context.Url.Id, facts));
    }

    /// <summary>
    /// Pure evaluation core: applies the oversized and format rules to explicit
    /// image facts. Composition sources that observe transferred bytes
    /// (rendered resource timing, future subresource capture) feed this method
    /// directly; the context adapter supplies DOM-declared facts.
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

            if (image.TransferredBytes is { } bytes
                && bytes > _options.OversizedBytesThreshold
                && oversizedReported.Add(src))
            {
                findings.Add(Finding(
                    ImageRuleKeys.Oversized,
                    FindingSeverity.Warning,
                    urlId,
                    new
                    {
                        src,
                        bytes,
                        threshold = _options.OversizedBytesThreshold,
                        declaredWidth = image.DeclaredWidth,
                        declaredHeight = image.DeclaredHeight,
                    }));
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
            // evidence only); the format check still applies.
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
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
            ? parsed
            : null;

    private static FindingDraft Finding(string ruleKey, FindingSeverity severity, long urlId, object evidence) =>
        new(ruleKey, AuditCategory.Performance, severity, urlId,
            JsonSerializer.Serialize(evidence, JsonOptions), RecommendationKeys.ForRule(ruleKey));
}
