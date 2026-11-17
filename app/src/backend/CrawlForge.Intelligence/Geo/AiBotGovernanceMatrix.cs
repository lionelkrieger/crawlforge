using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.Geo;

/// <summary>
/// AI-bot governance matrix (FS-CRAWL-002 §13.2, criterion #46): agent ×
/// scope × purpose allow/deny verdicts built from the BUNDLED agent-profile
/// catalogue cross-referenced with observed robots directives — robots.txt
/// rules (locked <see cref="IRobotsTxtParser"/>), X-Robots-Tag response
/// headers (stored header_directives inventory), and HTML meta robots /
/// agent-specific meta tags (stored bodies).
///
/// <para><b>ADVISORY ONLY:</b> the matrix is a report, never a gate. It NEVER
/// modifies robots.txt, never alters crawl behavior, and never contacts any
/// vendor. All inputs are stored artifacts; evaluation is fully offline.</para>
///
/// Purpose semantics: Search is denied by noindex/none; Rag and Training are
/// denied by noai/none (Training additionally by noimageai — image-generation
/// training opt-out). Agent-qualified directives (for example
/// "GPTBot: noai") apply only to that agent. Contradictions between robots
/// and header/meta directives emit <c>geo.governance-conflict</c> (Notice)
/// with both evidence values.
/// </summary>
public sealed class AiBotGovernanceMatrix : IAiBotGovernanceMatrix
{
    /// <summary>Bound on sampled URLs for URL-scope rows.</summary>
    public const int MaxSampledUrls = 50;

    /// <summary>Bound on evaluated path prefixes.</summary>
    public const int MaxPrefixes = 20;

    private const int MaxStoredBodyBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions EvidenceOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbProvider _provider;
    private readonly IContentStore _contentStore;
    private readonly IRobotsTxtParser _robotsParser;
    private readonly AiAgentProfileCatalogue _catalogue;
    private readonly ITolerantDomParser _domParser;

    /// <summary>Creates the advisory matrix over stored data only.</summary>
    /// <param name="provider">Database provider.</param>
    /// <param name="contentStore">Compressed content store (stored bodies).</param>
    /// <param name="robotsParser">Locked robots.txt parser (accepted keystone surface).</param>
    /// <param name="catalogue">Bundled AI-agent profile catalogue.</param>
    /// <param name="domParser">Tolerant DOM parser for meta directives.</param>
    public AiBotGovernanceMatrix(
        IDbProvider provider,
        IContentStore contentStore,
        IRobotsTxtParser robotsParser,
        AiAgentProfileCatalogue catalogue,
        ITolerantDomParser domParser)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _contentStore = contentStore ?? throw new ArgumentNullException(nameof(contentStore));
        _robotsParser = robotsParser ?? throw new ArgumentNullException(nameof(robotsParser));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _domParser = domParser ?? throw new ArgumentNullException(nameof(domParser));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GovernanceMatrixRowDto>> EvaluateAsync(
        long auditId,
        CancellationToken cancellationToken = default)
    {
        var (rows, _) = await EvaluateWithFindingsAsync(auditId, cancellationToken);
        return rows;
    }

    /// <summary>
    /// Evaluates the matrix and also returns governance-conflict findings.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matrix rows plus conflict findings.</returns>
    public async Task<(IReadOnlyList<GovernanceMatrixRowDto> Rows, IReadOnlyList<FindingDraft> Findings)> EvaluateWithFindingsAsync(
        long auditId,
        CancellationToken cancellationToken = default)
    {
        var urls = await LoadUrlsAsync(auditId, cancellationToken);
        var robotsByHost = await LoadRobotsDocumentsAsync(auditId, urls, cancellationToken);
        var prefixes = DerivePrefixes(urls, robotsByHost);
        var rows = new List<GovernanceMatrixRowDto>();
        var findings = new List<FindingDraft>();

        foreach (var profile in _catalogue.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Prefix scope: robots rules only (headers/meta are per-URL).
            foreach (var host in robotsByHost.Keys.OrderBy(h => h, StringComparer.Ordinal))
            {
                var document = robotsByHost[host];
                foreach (var prefix in prefixes)
                {
                    var verdict = _robotsParser.Evaluate(document, $"https://{host}{prefix}", profile.AgentToken);
                    var baseVerdict = verdict.MatchedRule is null
                        ? GovernanceVerdict.Unspecified
                        : verdict.Allowed ? GovernanceVerdict.Allow : GovernanceVerdict.Deny;
                    foreach (var purpose in new[] { AiAgentPurpose.Search, AiAgentPurpose.Rag, AiAgentPurpose.Training })
                    {
                        rows.Add(new GovernanceMatrixRowDto(
                            0, auditId, profile.AgentToken, $"{host}{prefix}", purpose, baseVerdict,
                            JsonSerializer.Serialize(new
                            {
                                scopeKind = "prefix",
                                host,
                                prefix,
                                matchedRule = verdict.MatchedRule is null
                                    ? null
                                    : new { allow = verdict.MatchedRule.Allow, path = verdict.MatchedRule.PathPattern },
                            }, EvidenceOptions)));
                    }
                }
            }

            // URL scope: robots + X-Robots-Tag + meta directives.
            foreach (var url in urls.Where(u => u.IsSample).OrderBy(u => u.Id))
            {
                var signals = await LoadUrlSignalsAsync(auditId, url, cancellationToken);
                var document = robotsByHost.GetValueOrDefault(url.Host) ?? new RobotsDocument([], []);
                var verdict = _robotsParser.Evaluate(document, url.CanonicalUrl, profile.AgentToken);
                var baseVerdict = verdict.MatchedRule is null
                    ? GovernanceVerdict.Unspecified
                    : verdict.Allowed ? GovernanceVerdict.Allow : GovernanceVerdict.Deny;

                var directives = CollectDirectives(signals, profile.AgentToken);
                foreach (var purpose in new[] { AiAgentPurpose.Search, AiAgentPurpose.Rag, AiAgentPurpose.Training })
                {
                    var overlay = OverlayFor(directives, purpose);
                    var final = overlay ?? baseVerdict;

                    if (overlay is { } overlayValue &&
                        ((baseVerdict == GovernanceVerdict.Allow && overlayValue == GovernanceVerdict.Deny) ||
                         (baseVerdict == GovernanceVerdict.Deny && overlayValue == GovernanceVerdict.Allow)))
                    {
                        findings.Add(new FindingDraft(
                            FindingRuleKeys.GeoGovernanceConflict, AuditCategory.AiSearchGeo, FindingSeverity.Notice,
                            url.Id,
                            JsonSerializer.Serialize(new
                            {
                                url = url.CanonicalUrl,
                                agent = profile.AgentToken,
                                purpose = purpose.ToString(),
                                robotsVerdict = baseVerdict.ToString(),
                                directiveVerdict = overlayValue.ToString(),
                                directives = directives.ToArray(),
                                xRobotsTag = signals.XRobotsTag,
                                metaRobots = signals.MetaRobots,
                            }, EvidenceOptions),
                            null));
                    }

                    rows.Add(new GovernanceMatrixRowDto(
                        0, auditId, profile.AgentToken, url.CanonicalUrl, purpose, final,
                        JsonSerializer.Serialize(new
                        {
                            scopeKind = "url",
                            urlId = url.Id,
                            host = url.Host,
                            matchedRule = verdict.MatchedRule is null
                                ? null
                                : new { allow = verdict.MatchedRule.Allow, path = verdict.MatchedRule.PathPattern },
                            directives = directives.ToArray(),
                        }, EvidenceOptions)));
                }
            }
        }

        var ordered = rows
            .OrderBy(r => r.AgentName, StringComparer.Ordinal)
            .ThenBy(r => r.ScopeValue, StringComparer.Ordinal)
            .ThenBy(r => r.Purpose)
            .ToArray();
        return (ordered, findings);
    }

    private static HashSet<string> CollectDirectives(UrlSignals signals, string agentToken)
    {
        var directives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { signals.XRobotsTag, signals.MetaRobots, signals.AgentMeta.GetValueOrDefault(agentToken) })
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (var part in source.Split(','))
            {
                var trimmed = part.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon > 0)
                {
                    var qualifier = trimmed[..colon].Trim();
                    if (!string.Equals(qualifier, agentToken, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // agent-qualified directive for a different agent
                    }

                    trimmed = trimmed[(colon + 1)..].Trim();
                }

                if (trimmed.Length > 0)
                {
                    directives.Add(trimmed.ToLowerInvariant());
                }
            }
        }

        return directives;
    }

    private static GovernanceVerdict? OverlayFor(HashSet<string> directives, AiAgentPurpose purpose)
    {
        bool Has(params string[] names) => names.Any(directives.Contains);

        if (Has("none"))
        {
            return GovernanceVerdict.Deny;
        }

        return purpose switch
        {
            AiAgentPurpose.Search => Has("noindex") ? GovernanceVerdict.Deny
                : Has("index", "all") ? GovernanceVerdict.Allow
                : null,
            AiAgentPurpose.Rag => Has("noai") ? GovernanceVerdict.Deny : null,
            AiAgentPurpose.Training => Has("noai", "noimageai") ? GovernanceVerdict.Deny : null,
            _ => null,
        };
    }

    private static IReadOnlyList<string> DerivePrefixes(
        IReadOnlyList<UrlFact> urls,
        IReadOnlyDictionary<string, RobotsDocument> robotsByHost)
    {
        var prefixes = new OrderedSet();
        prefixes.Add("/");

        // Directory prefixes from the URL tree (first path segment).
        foreach (var url in urls)
        {
            var segments = url.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                prefixes.Add("/" + segments[0] + "/");
            }
        }

        // Prefixes declared by robots rules.
        foreach (var document in robotsByHost.Values)
        foreach (var group in document.Groups)
        foreach (var rule in group.Rules)
        {
            var pattern = rule.PathPattern.Trim('*', '$');
            var lastSlash = pattern.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                prefixes.Add(pattern[..(lastSlash + 1)]);
            }
        }

        return prefixes.Take(MaxPrefixes).ToArray();
    }

    private async Task<IReadOnlyList<UrlFact>> LoadUrlsAsync(long auditId, CancellationToken cancellationToken)
    {
        var urls = new List<UrlFact>();
        var sampled = 0;
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, host, path, canonical_url, is_indexable, content_type FROM url_records " +
            "WHERE audit_id = @auditId ORDER BY id";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var isIndexable = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);
            var contentType = reader.IsDBNull(5) ? null : reader.GetString(5);
            var isHtml = contentType is null || contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
            var isSample = isIndexable && isHtml && sampled < MaxSampledUrls;
            if (isSample)
            {
                sampled++;
            }

            urls.Add(new UrlFact(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), isSample));
        }

        return urls;
    }

    private async Task<IReadOnlyDictionary<string, RobotsDocument>> LoadRobotsDocumentsAsync(
        long auditId,
        IReadOnlyList<UrlFact> urls,
        CancellationToken cancellationToken)
    {
        var documents = new Dictionary<string, RobotsDocument>(StringComparer.Ordinal);
        foreach (var host in urls.Select(u => u.Host).Distinct(StringComparer.Ordinal))
        {
            var robotsUrl = urls.FirstOrDefault(u =>
                u.Host == host && string.Equals(u.Path, "/robots.txt", StringComparison.OrdinalIgnoreCase));
            if (robotsUrl is null)
            {
                documents[host] = new RobotsDocument([], []);
                continue;
            }

            var body = await ReadStoredBodyAsync(auditId, robotsUrl.Id, cancellationToken);
            documents[host] = body is null ? new RobotsDocument([], []) : _robotsParser.Parse(body);
        }

        return documents;
    }

    private async Task<UrlSignals> LoadUrlSignalsAsync(long auditId, UrlFact url, CancellationToken cancellationToken)
    {
        string? xRobotsTag = null;
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT payload_json FROM url_elements WHERE url_id = @urlId AND element_group = 'header_directives'";
            AddParameter(command, "@urlId", url.Id);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is string payload)
            {
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    if (document.RootElement.TryGetProperty("xRobotsTag", out var tag) &&
                        tag.ValueKind == JsonValueKind.String)
                    {
                        xRobotsTag = tag.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Malformed inventory payload: treat as no header signal.
                }
            }
        }

        var metaRobots = new List<string>();
        var agentMeta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = await ReadStoredBodyAsync(auditId, url.Id, cancellationToken);
        if (body is not null)
        {
            var document = _domParser.Parse(body, url.CanonicalUrl);
            foreach (var meta in EnumerateTag(document.Root, "meta"))
            {
                if (!meta.Attributes.TryGetValue("name", out var name) ||
                    !meta.Attributes.TryGetValue("content", out var content))
                {
                    continue;
                }

                if (string.Equals(name, "robots", StringComparison.OrdinalIgnoreCase))
                {
                    metaRobots.Add(content);
                }
                else
                {
                    agentMeta[name] = content;
                }
            }
        }

        return new UrlSignals(
            xRobotsTag,
            metaRobots.Count > 0 ? string.Join(", ", metaRobots) : null,
            agentMeta);
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record UrlFact(long Id, string Host, string Path, string CanonicalUrl, bool IsSample);

    private sealed record UrlSignals(
        string? XRobotsTag,
        string? MetaRobots,
        IReadOnlyDictionary<string, string> AgentMeta);

    private sealed class OrderedSet
    {
        private readonly List<string> _order = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public void Add(string value)
        {
            if (_seen.Add(value))
            {
                _order.Add(value);
            }
        }

        public IEnumerable<string> Take(int count) => _order.Take(count);
    }
}
