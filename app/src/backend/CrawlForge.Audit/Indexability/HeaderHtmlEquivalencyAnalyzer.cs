using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// HTTP header vs HTML directive equivalency analyzer (FS-CRAWL-002 §15.4,
/// criterion #48 header facet). Reconciles X-Robots-Tag against meta robots
/// (per directive, per agent), Link rel="canonical" against the HTML canonical
/// link, and Link rel="alternate" hreflang against HTML hreflang alternates.
/// </summary>
/// <remarks>
/// <para>
/// Conflicts emit the locked rule keys
/// <c>indexability.header-html-conflict</c>, <c>canonical.header-html-conflict</c>,
/// and <c>hreflang.header-html-conflict</c> as <b>Error</b> findings with DUAL
/// evidence: both source values plus a directive-level diff. Conflicts are
/// high-severity because search engines may honor either source; the finding
/// never auto-resolves the conflict. Agreement records a Pass annotation.
/// </para>
/// <para>
/// Parsed header directives are exposed for the <c>header_directives</c>
/// element group through <see cref="BuildHeaderDirectivesInventory"/>
/// (FS-CRAWL-002 §15.5). Pure and offline: re-runs identically from stored
/// headers and bodies on re-audit.
/// </para>
/// </remarks>
public sealed class HeaderHtmlEquivalencyAnalyzer : IHeaderHtmlEquivalencyAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    private static readonly (string Negative, string Positive)[] OpposableDirectives =
    {
        ("noindex", "index"),
        ("nofollow", "follow"),
        ("noarchive", "archive"),
        ("nosnippet", "snippet"),
        ("notranslate", "translate"),
        ("noimageindex", "imageindex"),
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<FindingDraft>();
        ParsedHeaderDirectives headers = HeaderDirectiveParser.Parse(context.Fetch?.Headers);
        if (headers.Robots.Count == 0 && headers.Links.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
        }

        HtmlDirectives html = HtmlDirectiveExtractor.Extract(context.Document);
        long urlId = context.Url.Id;
        string pageUrl = context.Fetch?.FinalUrl ?? context.Url.CanonicalUrl;

        CompareRobotsDirectives(findings, headers, html, urlId);
        CompareCanonical(findings, headers, html, urlId, pageUrl);
        CompareHreflang(findings, headers, html, urlId, pageUrl);

        return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
    }

    /// <summary>
    /// Builds the <c>header_directives</c> element-group inventory item for
    /// the page (FS-CRAWL-002 §15.5). The pipeline persists the returned item
    /// alongside the DOM inventory; null when the response carried no
    /// parseable header directives.
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <returns>The inventory item, or null when nothing was parsed.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    public ElementInventoryItem? BuildHeaderDirectivesInventory(PageAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ParsedHeaderDirectives parsed = HeaderDirectiveParser.Parse(context.Fetch?.Headers);
        if (parsed.Robots.Count == 0 && parsed.Links.Count == 0)
        {
            return null;
        }

        return new ElementInventoryItem(
            ElementGroups.HeaderDirectives,
            JsonSerializer.Serialize(parsed, EvidenceOptions),
            Ordinal: 0);
    }

    private static void CompareRobotsDirectives(
        List<FindingDraft> findings,
        ParsedHeaderDirectives headers,
        HtmlDirectives html,
        long urlId)
    {
        foreach (RobotsHeaderDirective headerScope in headers.Robots)
        {
            HtmlRobotsDirective? htmlScope =
                html.Robots.FirstOrDefault(r => string.Equals(r.Agent, headerScope.Agent, StringComparison.OrdinalIgnoreCase))
                ?? (headerScope.Agent != HeaderDirectiveParser.GenericAgent
                    ? html.Robots.FirstOrDefault(r => r.Agent == HeaderDirectiveParser.GenericAgent)
                    : null);

            if (htmlScope is null)
            {
                // Header-only directives have no HTML counterpart to conflict with.
                continue;
            }

            HashSet<string> headerDirectives = ExpandDirectives(headerScope.Directives);
            HashSet<string> htmlDirectives = ExpandDirectives(htmlScope.Directives);
            bool anyConflict = false;

            foreach ((string negative, string positive) in OpposableDirectives)
            {
                bool headerNegative = headerDirectives.Contains(negative);
                bool headerPositive = headerDirectives.Contains(positive);
                bool htmlNegative = htmlDirectives.Contains(negative);
                bool htmlPositive = htmlDirectives.Contains(positive);

                string? conflict = null;
                if (headerNegative && htmlPositive)
                {
                    conflict = negative;
                }
                else if (headerPositive && htmlNegative)
                {
                    conflict = positive;
                }

                if (conflict is not null)
                {
                    anyConflict = true;
                    findings.Add(new FindingDraft(
                        FindingRuleKeys.IndexabilityHeaderHtmlConflict,
                        AuditCategory.Indexability,
                        FindingSeverity.Error,
                        urlId,
                        JsonSerializer.Serialize(new
                        {
                            agent = headerScope.Agent,
                            directive = conflict,
                            headerValue = headerScope.RawValue,
                            htmlValue = htmlScope.RawContent,
                            headerDirectives = headerScope.Directives,
                            htmlDirectives = htmlScope.Directives,
                            diff = new { header = conflict, html = conflict == negative ? positive : negative },
                        }, EvidenceOptions),
                        null));
                }
            }

            if (!anyConflict)
            {
                findings.Add(new FindingDraft(
                    FindingRuleKeys.IndexabilityHeaderHtmlConflict,
                    AuditCategory.Indexability,
                    FindingSeverity.Pass,
                    urlId,
                    JsonSerializer.Serialize(new
                    {
                        agent = headerScope.Agent,
                        headerValue = headerScope.RawValue,
                        htmlValue = htmlScope.RawContent,
                        agreement = true,
                    }, EvidenceOptions),
                    null));
            }
        }
    }

    private static void CompareCanonical(
        List<FindingDraft> findings,
        ParsedHeaderDirectives headers,
        HtmlDirectives html,
        long urlId,
        string pageUrl)
    {
        LinkHeaderEntry? headerCanonical = headers.Links.FirstOrDefault(l => l.Relation == "canonical");
        HtmlCanonicalLink? htmlCanonical = html.Canonicals.FirstOrDefault();
        if (headerCanonical is null || htmlCanonical is null)
        {
            return;
        }

        string? headerResolved = Resolve(pageUrl, headerCanonical.Target);
        string? htmlResolved = Resolve(pageUrl, htmlCanonical.Href);
        bool agree = headerResolved is not null
            && htmlResolved is not null
            && string.Equals(Normalize(headerResolved), Normalize(htmlResolved), StringComparison.OrdinalIgnoreCase);

        findings.Add(new FindingDraft(
            FindingRuleKeys.CanonicalHeaderHtmlConflict,
            AuditCategory.Indexability,
            agree ? FindingSeverity.Pass : FindingSeverity.Error,
            urlId,
            JsonSerializer.Serialize(new
            {
                headerValue = headerCanonical.RawValue,
                htmlValue = htmlCanonical.Href,
                headerTarget = headerCanonical.Target,
                htmlTarget = htmlCanonical.Href,
                resolvedHeader = headerResolved,
                resolvedHtml = htmlResolved,
                pageUrl,
                diff = agree ? null : new { header = headerResolved ?? headerCanonical.Target, html = htmlResolved ?? htmlCanonical.Href },
            }, EvidenceOptions),
            null));
    }

    private static void CompareHreflang(
        List<FindingDraft> findings,
        ParsedHeaderDirectives headers,
        HtmlDirectives html,
        long urlId,
        string pageUrl)
    {
        foreach (LinkHeaderEntry headerAlternate in headers.Links.Where(l => l.Relation == "alternate" && l.Hreflang is not null))
        {
            HtmlHreflangAlternate? htmlAlternate = html.HreflangAlternates.FirstOrDefault(
                h => string.Equals(h.Hreflang, headerAlternate.Hreflang, StringComparison.OrdinalIgnoreCase));
            if (htmlAlternate is null)
            {
                continue;
            }

            string? headerResolved = Resolve(pageUrl, headerAlternate.Target);
            string? htmlResolved = Resolve(pageUrl, htmlAlternate.Href);
            bool agree = headerResolved is not null
                && htmlResolved is not null
                && string.Equals(Normalize(headerResolved), Normalize(htmlResolved), StringComparison.OrdinalIgnoreCase);

            findings.Add(new FindingDraft(
                FindingRuleKeys.HreflangHeaderHtmlConflict,
                AuditCategory.StructuredData,
                agree ? FindingSeverity.Pass : FindingSeverity.Error,
                urlId,
                JsonSerializer.Serialize(new
                {
                    hreflang = headerAlternate.Hreflang,
                    headerValue = headerAlternate.RawValue,
                    htmlValue = htmlAlternate.Href,
                    headerTarget = headerAlternate.Target,
                    htmlTarget = htmlAlternate.Href,
                    resolvedHeader = headerResolved,
                    resolvedHtml = htmlResolved,
                    pageUrl,
                    diff = agree ? null : new { header = headerResolved ?? headerAlternate.Target, html = htmlResolved ?? htmlAlternate.Href },
                }, EvidenceOptions),
                null));
        }
    }

    private static HashSet<string> ExpandDirectives(IReadOnlyList<string> directives)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directive in directives)
        {
            // Directive parameters (max-snippet: n) compare by name.
            string name = directive.Split(':')[0].Trim();
            if (name == "none")
            {
                expanded.Add("noindex");
                expanded.Add("nofollow");
            }
            else if (name == "all")
            {
                expanded.Add("index");
                expanded.Add("follow");
            }
            else if (name.Length > 0)
            {
                expanded.Add(name);
            }
        }

        return expanded;
    }

    private static string? Resolve(string pageUrl, string href)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? baseUri)
            || !Uri.TryCreate(baseUri, href, out Uri? target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return target.AbsoluteUri;
    }

    private static string Normalize(string url) => url.TrimEnd('/');
}