using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Stable rule keys of the URL-structure rule family (FS-CRAWL-001 §7.4;
/// CAP-B-064 parameters, CAP-B-065 inconsistent trailing slashes, CAP-B-066
/// duplicate URLs). Additive keys in the open rule-key taxonomy; category
/// Indexability (URL structure is canonicalization/indexability-adjacent in
/// the locked <see cref="AuditCategory"/> enum — see the WP-09-05 handoff
/// notation note).
/// </summary>
public static class UrlStructureRuleKeys
{
    /// <summary>Session identifiers in the URL (query parameters or cookieless path segments).</summary>
    public const string SessionParameters = "url.session-parameters";

    /// <summary>Marketing/tracking parameters in the URL query.</summary>
    public const string TrackingParameters = "url.tracking-parameters";

    /// <summary>The same path is crawled both with and without a trailing slash.</summary>
    public const string TrailingSlashInconsistency = "url.trailing-slash-inconsistency";

    /// <summary>Distinct URL records normalize to the same resource (session/tracking parameters, fragments, host case, parameter order).</summary>
    public const string DuplicateUrls = "url.duplicate-urls";
}

/// <summary>
/// URL-structure rules (FS-CRAWL-001 §7.4; CAP-B-064/065/066). Per-URL rules
/// detect session and tracking parameters in the crawled URL; the cross-URL
/// site pass detects inconsistent trailing slashes and duplicate URLs over
/// the audit's URL records (§7.6 aggregation).
/// </summary>
/// <remarks>
/// <para>
/// Pure and offline: per-URL rules are a function of the stored URL record;
/// the site pass reads <c>url_records</c> only — no network, no bodies — so
/// re-audits recompute identically (§9.6). Thread-safe: no mutable instance
/// state. Deterministic: groups ordered by key, members by urlId.
/// </para>
/// <para>
/// Parameter vocabularies are bundled defaults (E2: no remote catalogue);
/// <see cref="UrlStructureOptions"/> extends them locally. Session parameters
/// create per-visitor duplicate URLs (Warning); tracking parameters fragment
/// analytics and canonicalization (Notice). Duplicate-URL normalization
/// strips session/tracking parameters and fragments, lowercases scheme/host,
/// drops default ports, and sorts the remaining query — it deliberately
/// PRESERVES trailing slashes (that variant pair is the trailing-slash rule).
/// </para>
/// </remarks>
public sealed partial class UrlStructureAnalyzer : IAuditRuleAnalyzer
{
    private const int PageSize = 500;
    private const int MaxGroupMembers = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly HashSet<string> SessionParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sid", "sessionid", "session_id", "phpsessid", "jsessionid", "cfid", "cftoken",
        "zenid", "oscsid", "bitrix_sessid", "phpauth_pw", "phpauth_username",
        "sessionguid", "requestsessionid", "session",
    };

    private static readonly string[] SessionParameterPrefixes =
    {
        "aspsessionid", "asp.net_sessionid",
    };

    private static readonly HashSet<string> TrackingParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "gclid", "gclsrc", "fbclid", "msclkid", "dclid", "wbraid", "gbraid",
        "mc_eid", "mc_cid", "igshid", "yclid", "ysclid", "twclid", "ttclid",
        "epik", "dj_eid", "mkt_tok", "s_kwcid", "ef_id", "_ga", "_gl", "sccid",
        "li_fat_id", "sms_click_id", "dblclick_id", "irclickid", "clickid",
        "rb_clickid", "ved", "guccounter", "__hssc", "__hstc", "__hsfp", "_hsmi",
        "hsctatracking", "trk_contact", "trk_msg", "trk_module", "trk_sid",
    };

    private static readonly string[] TrackingParameterPrefixes =
    {
        "utm_",
    };

    private readonly UrlStructureOptions _options;
    private readonly IReadRepository<UrlRecordDto>? _urls;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="urls">
    /// Optional URL-record repository; required for the cross-URL site pass
    /// (<see cref="AnalyzeSiteAsync"/>). Per-URL rules work without it.
    /// </param>
    /// <param name="options">Local parameter-vocabulary extensions.</param>
    public UrlStructureAnalyzer(IReadRepository<UrlRecordDto>? urls = null, UrlStructureOptions? options = null)
    {
        _urls = urls;
        _options = options ?? UrlStructureOptions.Default;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys =>
    [
        UrlStructureRuleKeys.SessionParameters,
        UrlStructureRuleKeys.TrackingParameters,
        UrlStructureRuleKeys.TrailingSlashInconsistency,
        UrlStructureRuleKeys.DuplicateUrls,
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<FindingDraft>();
        if (!TryParseHttpUrl(context.Url.CanonicalUrl, out var uri))
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
        }

        var sessionParameters = new List<object>();
        var trackingParameters = new List<object>();
        foreach (var (name, value) in ParseQueryParameters(uri.Query))
        {
            if (IsSessionParameter(name))
            {
                sessionParameters.Add(new { name, value });
            }
            else if (IsTrackingParameter(name))
            {
                trackingParameters.Add(new { name, value });
            }
        }

        var pathSegments = CookielessSessionPattern().Matches(uri.AbsolutePath)
            .Select(match => match.Value)
            .ToArray();

        if (sessionParameters.Count > 0 || pathSegments.Length > 0)
        {
            findings.Add(Finding(
                UrlStructureRuleKeys.SessionParameters,
                FindingSeverity.Warning,
                context.Url.Id,
                new
                {
                    url = context.Url.CanonicalUrl,
                    parameters = sessionParameters,
                    pathSegments,
                }));
        }

        if (trackingParameters.Count > 0)
        {
            findings.Add(Finding(
                UrlStructureRuleKeys.TrackingParameters,
                FindingSeverity.Notice,
                context.Url.Id,
                new
                {
                    url = context.Url.CanonicalUrl,
                    parameters = trackingParameters,
                }));
        }

        return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
    }

    /// <summary>
    /// Cross-URL site pass (§7.6): inconsistent trailing slashes and duplicate
    /// URLs over the audit's stored URL records. D14 invocation pattern — the
    /// engine calls this once after the per-URL passes.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Site-wide findings grouping all affected URLs.</returns>
    /// <exception cref="InvalidOperationException">No URL-record repository was supplied.</exception>
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeSiteAsync(long auditId, CancellationToken cancellationToken = default)
    {
        if (_urls is null)
        {
            throw new InvalidOperationException("The URL-structure site pass requires an IReadRepository<UrlRecordDto>.");
        }

        var slashGroups = new Dictionary<string, SlashGroup>(StringComparer.Ordinal);
        var duplicateGroups = new Dictionary<string, List<UrlMember>>(StringComparer.Ordinal);

        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _urls.ListAsync(new ListQuery(
                Offset: offset,
                Limit: PageSize,
                Filters: new Dictionary<string, string>
                {
                    ["auditId"] = auditId.ToString(CultureInfo.InvariantCulture),
                },
                SortBy: "id"), cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var record in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseHttpUrl(record.CanonicalUrl, out var uri))
                {
                    continue;
                }

                var member = new UrlMember(record.Id, record.CanonicalUrl);

                // Trailing-slash consistency: same host + path (slash-stripped)
                // + query reachable both with and without the trailing slash.
                var trimmedPath = uri.AbsolutePath.TrimEnd('/');
                if (trimmedPath.Length > 0)
                {
                    var key = string.Concat(
                        uri.Host.ToLowerInvariant(), "|", trimmedPath, "|", uri.Query);
                    if (!slashGroups.TryGetValue(key, out var group))
                    {
                        group = new SlashGroup(uri.Host.ToLowerInvariant(), trimmedPath, uri.Query);
                        slashGroups[key] = group;
                    }

                    if (uri.AbsolutePath.EndsWith('/'))
                    {
                        group.WithSlash.Add(member);
                    }
                    else
                    {
                        group.WithoutSlash.Add(member);
                    }
                }

                // Duplicate URLs: normalization strips session/tracking
                // parameters and fragments, lowercases scheme/host, drops
                // default ports, sorts the remaining query.
                var normalized = NormalizeUrl(uri);
                if (!duplicateGroups.TryGetValue(normalized, out var duplicates))
                {
                    duplicates = [];
                    duplicateGroups[normalized] = duplicates;
                }

                duplicates.Add(member);
            }

            offset += page.Items.Count;
            if (offset >= page.TotalCount)
            {
                break;
            }
        }

        var findings = new List<FindingDraft>();
        foreach (var (_, group) in slashGroups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.WithSlash.Count == 0 || group.WithoutSlash.Count == 0)
            {
                continue;
            }

            var members = group.WithSlash.Concat(group.WithoutSlash).OrderBy(m => m.UrlId).ToArray();
            findings.Add(Finding(
                UrlStructureRuleKeys.TrailingSlashInconsistency,
                FindingSeverity.Warning,
                urlId: null,
                new
                {
                    host = group.Host,
                    path = group.Path,
                    query = group.Query,
                    urlCount = members.Length,
                    urls = members.Select(m => new { urlId = m.UrlId, url = m.Url, trailingSlash = m.Url.EndsWith('/') }).ToArray(),
                }));
        }

        foreach (var (normalized, members) in duplicateGroups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (members.Count < 2)
            {
                continue;
            }

            var ordered = members.OrderBy(m => m.UrlId).ToArray();
            var bounded = ordered.Take(MaxGroupMembers).Select(m => new { urlId = m.UrlId, url = m.Url }).ToArray();
            findings.Add(Finding(
                UrlStructureRuleKeys.DuplicateUrls,
                FindingSeverity.Warning,
                urlId: null,
                new
                {
                    normalizedUrl = normalized,
                    urlCount = ordered.Length,
                    membersTruncated = ordered.Length > bounded.Length,
                    urls = bounded,
                }));
        }

        return findings;
    }

    private bool IsSessionParameter(string name) =>
        SessionParameterNames.Contains(name)
        || SessionParameterPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || (_options.AdditionalSessionParameters ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);

    private bool IsTrackingParameter(string name) =>
        TrackingParameterNames.Contains(name)
        || TrackingParameterPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || (_options.AdditionalTrackingParameters ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);

    private string NormalizeUrl(Uri uri)
    {
        var builder = new StringBuilder();
        builder.Append(uri.Scheme.ToLowerInvariant()).Append("://").Append(uri.Host.ToLowerInvariant());
        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append(uri.AbsolutePath);

        var kept = ParseQueryParameters(uri.Query)
            .Where(p => !IsSessionParameter(p.Name) && !IsTrackingParameter(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => string.Concat(
                Uri.EscapeDataString(p.Name), "=", Uri.EscapeDataString(p.Value)))
            .ToArray();
        if (kept.Length > 0)
        {
            builder.Append('?').Append(string.Join('&', kept));
        }

        return builder.ToString();
    }

    private static IEnumerable<(string Name, string Value)> ParseQueryParameters(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            yield break;
        }

        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            yield break;
        }

        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            yield return (Decode(name), Decode(value));
        }
    }

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool TryParseHttpUrl(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static FindingDraft Finding(string ruleKey, FindingSeverity severity, long? urlId, object evidence) =>
        new(ruleKey, AuditCategory.Indexability, severity, urlId,
            JsonSerializer.Serialize(evidence, JsonOptions), RecommendationKeys.ForRule(ruleKey));

    [GeneratedRegex(@"\((?:S|A|F)\([^)]*\)\)", RegexOptions.Compiled)]
    private static partial Regex CookielessSessionPattern();

    private sealed record UrlMember(long UrlId, string Url);

    private sealed class SlashGroup
    {
        public SlashGroup(string host, string path, string query)
        {
            Host = host;
            Path = path;
            Query = query;
        }

        public string Host { get; }

        public string Path { get; }

        public string Query { get; }

        public List<UrlMember> WithSlash { get; } = [];

        public List<UrlMember> WithoutSlash { get; } = [];
    }
}

/// <summary>
/// Local URL-structure configuration (E2: bundled defaults, local extensions
/// only — no remote catalogue).
/// </summary>
/// <param name="AdditionalSessionParameters">Extra query-parameter names treated as session identifiers.</param>
/// <param name="AdditionalTrackingParameters">Extra query-parameter names treated as tracking parameters.</param>
public sealed record UrlStructureOptions(
    IReadOnlyList<string>? AdditionalSessionParameters = null,
    IReadOnlyList<string>? AdditionalTrackingParameters = null)
{
    /// <summary>Default options (bundled vocabularies only).</summary>
    public static UrlStructureOptions Default { get; } = new();
}
