using System.Text;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Technology detector (FS-CRAWL-001 §7.15, criterion #29): matches response
/// headers, cookies, meta generators, script sources, and HTML markers against
/// the bundled <see cref="TechnologyFingerprintCatalogue"/> ONLY. Confidence is
/// the capped sum of matched signature weights. Detection is fully offline —
/// the catalogue is a local asset and no remote lookup exists (E2/E4).
/// </summary>
public sealed class TechnologyDetector : ITechnologyDetector
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly TechnologyFingerprintCatalogue _catalogue;
    private readonly ITolerantDomParser _parser;

    /// <summary>Creates the detector over the bundled catalogue and shared DOM parser.</summary>
    /// <param name="catalogue">Loaded bundled fingerprint catalogue.</param>
    /// <param name="parser">Tolerant parser used when only a raw body is available.</param>
    public TechnologyDetector(TechnologyFingerprintCatalogue catalogue, ITolerantDomParser parser)
    {
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TechnologyDetectionDraft>> DetectAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pageUrl = context.Url.CanonicalUrl;

        var rawHtml = ReadRawHtml(context);
        var document = context.Document ?? context.RenderedDocument ??
            (rawHtml is not null ? _parser.Parse(rawHtml, pageUrl) : null);

        var nodes = document is not null ? Walk(document.Root).ToList() : [];
        var headers = context.Fetch?.Headers;
        var setCookie = headers is null
            ? null
            : headers.FirstOrDefault(h => string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)).Value;

        var detections = new List<TechnologyDetectionDraft>();
        foreach (var technology in _catalogue.Technologies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var confidence = ComputeConfidence(technology, headers, setCookie, nodes, rawHtml);
            if (confidence > 0)
            {
                detections.Add(new TechnologyDetectionDraft(technology.Key, technology.Category, confidence));
            }
        }

        var ordered = detections
            .OrderByDescending(d => d.Confidence)
            .ThenBy(d => d.TechnologyKey, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<TechnologyDetectionDraft>>(ordered);
    }

    private static double ComputeConfidence(
        TechnologyFingerprint technology,
        IReadOnlyDictionary<string, string>? headers,
        string? setCookie,
        List<DomNode> nodes,
        string? rawHtml)
    {
        var total = 0.0;

        foreach (var signature in technology.Headers)
        {
            var value = headers?.FirstOrDefault(h => string.Equals(h.Key, signature.Name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is not null && Matches(value, signature.Pattern))
            {
                total += signature.Weight;
            }
        }

        if (setCookie is not null)
        {
            foreach (var signature in technology.Cookies)
            {
                var pattern = signature.IsPrefix
                    ? $@"(?:^|[,;]\s*){Regex.Escape(signature.Name)}[^=;,]*="
                    : $@"(?:^|[,;]\s*){Regex.Escape(signature.Name)}=";
                if (Matches(setCookie, pattern))
                {
                    total += signature.Weight;
                }
            }
        }

        foreach (var signature in technology.Meta)
        {
            var matched = nodes.Any(n =>
                n.TagName == "meta" &&
                n.Attributes.TryGetValue("name", out var name) &&
                string.Equals(name.Trim(), signature.Name, StringComparison.OrdinalIgnoreCase) &&
                n.Attributes.TryGetValue("content", out var content) &&
                Matches(content, signature.Pattern));
            if (matched)
            {
                total += signature.Weight;
            }
        }

        foreach (var signature in technology.Scripts)
        {
            var matched = nodes.Any(n =>
                n.TagName == "script" &&
                n.Attributes.TryGetValue("src", out var src) &&
                Matches(src, signature.Pattern));
            if (matched)
            {
                total += signature.Weight;
            }
        }

        if (rawHtml is not null)
        {
            foreach (var signature in technology.Html)
            {
                if (Matches(rawHtml, signature.Pattern))
                {
                    total += signature.Weight;
                }
            }
        }

        return Math.Min(1.0, total);
    }

    private static bool Matches(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            // Tolerant posture: a pathological pattern never fails the audit.
            return false;
        }
    }

    private static string? ReadRawHtml(PageAuditContext context)
    {
        if (context.Fetch is { Body.Length: > 0 })
        {
            return Encoding.UTF8.GetString(context.Fetch.Body.Span);
        }

        return null;
    }

    private static IEnumerable<DomNode> Walk(DomNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
