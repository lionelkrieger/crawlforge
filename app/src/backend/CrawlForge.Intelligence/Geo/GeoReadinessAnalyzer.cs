using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.Geo;

/// <summary>One GEO readiness check score (metric dimension payload).</summary>
/// <param name="Check">Stable check name.</param>
/// <param name="Score">Check score (0–1).</param>
public sealed record GeoCheckScore(string Check, double Score);

/// <summary>
/// AI-search (GEO) readiness analyzer (FS-CRAWL-002 §13, criterion #46):
/// per-host llms.txt / llms-full.txt discovery and validation from STORED
/// artifacts (offline re-validation, no re-fetch), link verification against
/// the CRAWLED URL set, and readiness scoring over five checks — llms-txt,
/// llms-links, answer-first structure, passage citability, entity clarity.
/// Findings use the additive AI Search (GEO) category and follow §7.5
/// suppression. All analysis is local; nothing is published or submitted
/// (exclusion E9).
/// </summary>
public sealed class GeoReadinessAnalyzer : IGeoReadinessAnalyzer
{
    /// <summary>Bound on stored-body reads (bytes).</summary>
    public const int MaxStoredBodyBytes = 2 * 1024 * 1024;

    /// <summary>Bound on the readiness page sample.</summary>
    public const int MaxReadinessSample = 50;

    /// <summary>Check name: well-formed llms.txt coverage across hosts.</summary>
    public const string CheckLlmsTxt = "llms-txt";

    /// <summary>Check name: llms.txt link health.</summary>
    public const string CheckLlmsLinks = "llms-links";

    /// <summary>Check name: answer-first page structure.</summary>
    public const string CheckAnswerFirst = "answer-first";

    /// <summary>Check name: citable passage presence.</summary>
    public const string CheckPassageCitability = "passage-citability";

    /// <summary>Check name: entity clarity (structured data / site identity).</summary>
    public const string CheckEntityClarity = "entity-clarity";

    private static readonly JsonSerializerOptions EvidenceOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbProvider _provider;
    private readonly IContentStore _contentStore;
    private readonly ITolerantDomParser _parser;

    /// <summary>Creates the analyzer over stored data only.</summary>
    /// <param name="provider">Database provider.</param>
    /// <param name="contentStore">Compressed content store (stored bodies).</param>
    /// <param name="parser">Tolerant DOM parser for readiness page checks.</param>
    public GeoReadinessAnalyzer(IDbProvider provider, IContentStore contentStore, ITolerantDomParser parser)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _contentStore = contentStore ?? throw new ArgumentNullException(nameof(contentStore));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <inheritdoc />
    public async Task<GeoReadinessResult> AnalyzeAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var (result, _) = await AnalyzeWithChecksAsync(auditId, cancellationToken);
        return result;
    }

    /// <summary>
    /// Analyzes readiness and also returns the per-check scores feeding the
    /// <c>geo-readiness</c> metric (dimension: check).
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The locked readiness result plus per-check scores.</returns>
    public async Task<(GeoReadinessResult Result, IReadOnlyList<GeoCheckScore> Checks)> AnalyzeWithChecksAsync(
        long auditId,
        CancellationToken cancellationToken = default)
    {
        var dataset = await LoadDatasetAsync(auditId, cancellationToken);
        var findings = new List<FindingDraft>();
        var hostResults = new List<LlmsTxtValidation>();
        var wellFormedHosts = 0;
        var totalLinks = 0;
        var brokenLinks = 0;

        foreach (var host in dataset.Hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dataset.LlmsByHost.TryGetValue(host, out var llms);
            dataset.LlmsFullByHost.TryGetValue(host, out var llmsFull);

            if (llms is null || llms.StatusCode != 200)
            {
                findings.Add(Draft(FindingRuleKeys.GeoLlmsTxtMissing, FindingSeverity.Notice, llms?.Id,
                    new { host, file = "llms.txt", status = llms?.StatusCode }));
                hostResults.Add(new LlmsTxtValidation(host, false, false, [], []));
                continue;
            }

            var (validation, linkCount) = await ValidateStoredAsync(auditId, llms, host, dataset, findings, cancellationToken);
            hostResults.Add(validation);
            if (validation.IsWellFormed)
            {
                wellFormedHosts++;
            }

            totalLinks += linkCount;
            brokenLinks += validation.BrokenLinks.Count;

            if (llmsFull is null || llmsFull.StatusCode != 200)
            {
                // §13.1: absent llms-full.txt while llms.txt exists is a Notice annotation.
                findings.Add(Draft(FindingRuleKeys.GeoLlmsTxtMissing, FindingSeverity.Notice, llmsFull?.Id,
                    new { host, file = "llms-full.txt", annotation = true, status = llmsFull?.StatusCode }));
            }
            else
            {
                var (fullValidation, fullLinkCount) = await ValidateStoredAsync(auditId, llmsFull, host, dataset, findings, cancellationToken);
                totalLinks += fullLinkCount;
                brokenLinks += fullValidation.BrokenLinks.Count;
            }
        }

        var sampleChecks = await RunSampleChecksAsync(auditId, dataset, cancellationToken);
        var hostCount = dataset.Hosts.Count;
        var checks = new List<GeoCheckScore>
        {
            new(CheckLlmsTxt, hostCount > 0 ? (double)wellFormedHosts / hostCount : 0),
            new(CheckLlmsLinks, totalLinks > 0 ? 1.0 - ((double)brokenLinks / totalLinks) : (hostCount > 0 ? 1 : 0)),
            new(CheckAnswerFirst, sampleChecks.AnswerFirst),
            new(CheckPassageCitability, sampleChecks.PassageCitability),
            new(CheckEntityClarity, sampleChecks.EntityClarity),
        };
        var readiness = dataset.UrlCount > 0
            ? Math.Round(100.0 * checks.Average(c => c.Score), 2)
            : 0.0;

        var result = new GeoReadinessResult(readiness, hostResults, findings);
        return (result, checks);
    }

    /// <inheritdoc />
    public LlmsTxtValidation ValidateLlmsTxt(string body, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(body);
        var host = string.Empty;
        if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            host = baseUri.Host;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new LlmsTxtValidation(host, false, false, [], ["empty-body: llms.txt body is empty"]);
        }

        var document = LlmsTxtParser.Parse(body, baseUrl);
        var issues = new List<string>();
        if (!document.HasH1)
        {
            issues.Add("missing-h1: no H1 title line ('# ...')");
        }

        if (!document.HasBlockquote)
        {
            issues.Add("missing-blockquote: no blockquote summary ('> ...')");
        }

        if (!document.HasH2SectionsWithLinks)
        {
            issues.Add("missing-sections: no H2 section with a markdown link list");
        }

        var broken = document.Links.Where(l => l.ResolvedUrl is null).Select(l => l.Url).ToArray();
        return new LlmsTxtValidation(host, true, issues.Count == 0, broken, issues);
    }

    private async Task<(LlmsTxtValidation Validation, int LinkCount)> ValidateStoredAsync(
        long auditId,
        UrlFact record,
        string host,
        Dataset dataset,
        List<FindingDraft> findings,
        CancellationToken cancellationToken)
    {
        var body = await ReadStoredBodyAsync(auditId, record.Id, cancellationToken);
        if (body is null)
        {
            return (new LlmsTxtValidation(host, true, false, [],
                ["stored-body-unavailable: no body artifact for the llms file"]), 0);
        }

        var validation = ValidateLlmsTxt(body, record.CanonicalUrl);
        if (!validation.IsWellFormed)
        {
            findings.Add(Draft(FindingRuleKeys.GeoLlmsTxtMalformed, FindingSeverity.Warning, record.Id,
                new { host, file = record.FileName, issues = validation.Issues }));
        }

        // Link verification against the CRAWLED URL set (§13.1): links are
        // enqueued at crawl time; offline, a target outside the crawled set or
        // with a non-success status is broken.
        var document = LlmsTxtParser.Parse(body, record.CanonicalUrl);
        var broken = new List<string>(validation.BrokenLinks);
        foreach (var link in document.Links)
        {
            if (link.ResolvedUrl is null)
            {
                continue; // already counted as structurally broken
            }

            string? reason = null;
            int? status = null;
            if (dataset.CrawledStatuses.TryGetValue(link.ResolvedUrl, out var crawled))
            {
                status = crawled;
                if (crawled != (int)CrawlStatusClass.Success)
                {
                    reason = "non-success-status";
                }
            }
            else
            {
                reason = "not-in-crawled-set";
            }

            if (reason is not null)
            {
                broken.Add(link.ResolvedUrl);
                findings.Add(Draft(FindingRuleKeys.GeoLlmsTxtBrokenLink, FindingSeverity.Warning, record.Id,
                    new { host, file = record.FileName, link = link.Text, target = link.ResolvedUrl, reason, status }));
            }
        }

        return (validation with { BrokenLinks = broken }, document.Links.Count);
    }

    private async Task<SampleCheckResult> RunSampleChecksAsync(
        long auditId,
        Dataset dataset,
        CancellationToken cancellationToken)
    {
        var sampled = 0;
        var answerFirst = 0;
        var citable = 0;
        var entities = 0;

        foreach (var url in dataset.SampleCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await ReadStoredBodyAsync(auditId, url.Id, cancellationToken);
            if (body is null)
            {
                continue;
            }

            sampled++;
            var document = _parser.Parse(body, url.CanonicalUrl);
            var bodyNode = FindBody(document.Root);
            if (IsAnswerFirst(bodyNode))
            {
                answerFirst++;
            }

            if (HasCitablePassage(bodyNode))
            {
                citable++;
            }

            if (HasEntityClarity(body))
            {
                entities++;
            }
        }

        return sampled == 0
            ? new SampleCheckResult(0, 0, 0)
            : new SampleCheckResult(
                (double)answerFirst / sampled,
                (double)citable / sampled,
                (double)entities / sampled);
    }

    private static DomNode FindBody(DomNode root)
    {
        foreach (var child in root.Children)
        {
            if (child.TagName == "body")
            {
                return child;
            }

            if (child.TagName == "html")
            {
                foreach (var grand in child.Children)
                {
                    if (grand.TagName == "body")
                    {
                        return grand;
                    }
                }
            }
        }

        return root;
    }

    private static bool IsAnswerFirst(DomNode bodyNode)
    {
        // Answer-first: a substantive paragraph (>= 40 chars) appears among
        // the first eight body elements, before deep section structure.
        foreach (var child in bodyNode.Children.Take(8))
        {
            if (child.TagName == "p" && child.TextContent.Trim().Length >= 40)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCitablePassage(DomNode bodyNode)
    {
        // Citable passage: a paragraph of 30–120 words (bounded, self-contained).
        foreach (var paragraph in EnumerateTag(bodyNode, "p"))
        {
            var words = paragraph.TextContent.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words is >= 30 and <= 120)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<DomNode> EnumerateTag(DomNode node, string tag)
    {
        if (node.TagName == tag)
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var match in EnumerateTag(child, tag))
            {
                yield return match;
            }
        }
    }

    private static bool HasEntityClarity(string body)
    {
        // Entity clarity: JSON-LD entities or an explicit site identity
        // (og:site_name) give AI engines a clear entity to cite.
        if (body.Contains("application/ld+json", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("@type", StringComparison.Ordinal))
        {
            return true;
        }

        return body.Contains("og:site_name", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Dataset> LoadDatasetAsync(long auditId, CancellationToken cancellationToken)
    {
        var dataset = new Dataset();
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, host, path, canonical_url, final_status_code, status_class, is_indexable, content_type " +
            "FROM url_records WHERE audit_id = @auditId ORDER BY id";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var host = reader.GetString(1);
            var path = reader.GetString(2);
            var canonical = reader.GetString(3);
            var statusCode = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
            var statusClass = reader.GetInt32(5);
            var isIndexable = Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture);
            var contentType = reader.IsDBNull(7) ? null : reader.GetString(7);

            dataset.UrlCount++;
            dataset.Hosts.Add(host);
            dataset.CrawledStatuses[canonical] = statusClass;

            if (string.Equals(path, "/llms.txt", StringComparison.OrdinalIgnoreCase))
            {
                dataset.LlmsByHost[host] = new UrlFact(id, canonical, statusCode, "llms.txt");
            }
            else if (string.Equals(path, "/llms-full.txt", StringComparison.OrdinalIgnoreCase))
            {
                dataset.LlmsFullByHost[host] = new UrlFact(id, canonical, statusCode, "llms-full.txt");
            }
            else if (isIndexable && dataset.SampleCandidates.Count < MaxReadinessSample &&
                (contentType is null || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)))
            {
                dataset.SampleCandidates.Add(new SampleUrl(id, canonical));
            }
        }

        return dataset;
    }

    private async Task<string?> ReadStoredBodyAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        string? storageKey;
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT storage_key FROM content_artifacts WHERE audit_id = @auditId AND url_id = @urlId AND kind = 0";
            AddParameter(command, "@auditId", auditId);
            AddParameter(command, "@urlId", urlId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            storageKey = result as string;
        }

        if (storageKey is null)
        {
            return null;
        }

        await using var stream = await _contentStore.OpenReadAsync(storageKey, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            var allowed = Math.Min(read, MaxStoredBodyBytes - total);
            if (allowed <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, allowed);
            total += allowed;
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static FindingDraft Draft(string ruleKey, FindingSeverity severity, long? urlId, object evidence) =>
        new(ruleKey, AuditCategory.AiSearchGeo, severity, urlId,
            JsonSerializer.Serialize(evidence, EvidenceOptions), null);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record UrlFact(long Id, string CanonicalUrl, int? StatusCode, string FileName);

    private sealed record SampleUrl(long Id, string CanonicalUrl);

    private sealed record SampleCheckResult(double AnswerFirst, double PassageCitability, double EntityClarity);

    private sealed class Dataset
    {
        public int UrlCount { get; set; }

        public OrderedSet Hosts { get; } = new();

        public Dictionary<string, int> CrawledStatuses { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, UrlFact> LlmsByHost { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, UrlFact> LlmsFullByHost { get; } = new(StringComparer.Ordinal);

        public List<SampleUrl> SampleCandidates { get; } = [];
    }

    private sealed class OrderedSet
    {
        private readonly List<string> _order = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public int Count => _order.Count;

        public void Add(string value)
        {
            if (_seen.Add(value))
            {
                _order.Add(value);
            }
        }

        public IEnumerator<string> GetEnumerator() => _order.GetEnumerator();
    }
}
