using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Emits the robots-blocked finding at crawl time (FS-CRAWL-001 §4.4/§5.4,
/// fixture trap <c>robots-blocked</c>). Blocked URLs are never fetched and
/// therefore never reach the offline indexability analyzer — the engine must
/// report them when the robots verdict disallows the fetch, with the
/// disallowing rule as evidence. The rule key and category match the offline
/// <c>IndexabilityAnalyzer</c> output (one stable key, two emission paths).
/// </summary>
public static class RobotsBlockFindingFactory
{
    /// <summary>Block-reason sub-classification for robots-blocked URL records (§2.6 block reasons).</summary>
    public const string RobotsBlockReason = "robots";

    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the robots-blocked finding with the disallowing rule evidence:
    /// the matched rule, the selecting user-agent group, the robots source URL
    /// and the evaluated user-agent.
    /// </summary>
    /// <param name="urlId">URL record of the blocked URL.</param>
    /// <param name="url">The canonical URL that was blocked.</param>
    /// <param name="verdict">The disallow verdict (must not be allowed).</param>
    /// <param name="document">The effective robots document.</param>
    /// <param name="userAgent">User-agent token the verdict was evaluated under.</param>
    /// <param name="robotsUrl">URL of the robots.txt the rules came from.</param>
    /// <returns>A Warning finding draft in the Indexability category.</returns>
    /// <exception cref="ArgumentException">The verdict allows the URL.</exception>
    public static FindingDraft Create(
        long urlId,
        string url,
        RobotsVerdict verdict,
        RobotsDocument document,
        string userAgent,
        string robotsUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(robotsUrl);
        if (verdict.Allowed)
        {
            throw new ArgumentException("Only disallow verdicts produce robots-blocked findings.", nameof(verdict));
        }

        var group = RobotsTxtParser.SelectGroup(document, userAgent);
        var evidence = JsonSerializer.Serialize(new
        {
            url,
            robotsState = RobotsState.Disallowed.ToString(),
            userAgent,
            robotsUrl,
            matchedRule = verdict.MatchedRule is { } rule
                ? new { allow = rule.Allow, pathPattern = rule.PathPattern }
                : null,
            userAgentGroup = group?.UserAgents,
            crawlDelaySeconds = group?.CrawlDelaySeconds,
        }, EvidenceOptions);

        return new FindingDraft(
            CrawlFindingRuleKeys.RobotsBlocked,
            AuditCategory.Indexability,
            FindingSeverity.Warning,
            urlId,
            evidence,
            RecommendationKey: null);
    }

    /// <summary>
    /// Classifies a URL record as robots-blocked per the spec state taxonomy:
    /// <c>status_class = Blocked</c> with block reason <c>robots</c>,
    /// <c>robots_state = Disallowed</c> and the RobotsBlocked indexability
    /// flag; the URL is not indexable and is never fetched.
    /// </summary>
    /// <param name="record">The URL record written for the blocked URL.</param>
    /// <returns>The classified record.</returns>
    public static UrlRecordDto Classify(UrlRecordDto record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            StatusClass = CrawlStatusClass.Blocked,
            BlockReason = RobotsBlockReason,
            RobotsState = RobotsState.Disallowed,
            IndexabilityFlags = record.IndexabilityFlags | IndexabilityFlags.RobotsBlocked,
            IsIndexable = false
        };
    }
}
