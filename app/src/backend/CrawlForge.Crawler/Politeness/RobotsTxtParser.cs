using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Tolerant robots.txt parser and evaluator (FS-CRAWL-001 §4.4): groups of
/// user-agent lines with Allow/Disallow rules, per-group crawl-delay, and
/// global Sitemap discovery. Evaluation follows the standard precedence:
/// the most specific matching user-agent group wins, then the longest
/// matching path pattern wins, with Allow breaking length ties. Patterns
/// support the <c>*</c> wildcard and the <c>$</c> end anchor. Malformed lines
/// are ignored; parsing never throws on hostile input and never consults the
/// network.
/// </summary>
public sealed class RobotsTxtParser : IRobotsTxtParser
{
    /// <inheritdoc/>
    public RobotsDocument Parse(string robotsBody)
    {
        ArgumentNullException.ThrowIfNull(robotsBody);

        var groups = new List<GroupBuilder>();
        var sitemaps = new List<string>();
        GroupBuilder? current = null;
        var lastLineWasRule = false;

        foreach (var rawLine in robotsBody.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var field = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();

            switch (field)
            {
                case "user-agent":
                    if (value.Length == 0)
                    {
                        break;
                    }

                    if (current is null || lastLineWasRule)
                    {
                        current = new GroupBuilder();
                        groups.Add(current);
                    }

                    current.UserAgents.Add(value);
                    lastLineWasRule = false;
                    break;

                case "allow":
                case "disallow":
                    if (value.Length == 0)
                    {
                        // An empty Disallow imposes no restriction; an empty Allow adds nothing.
                        lastLineWasRule = true;
                        break;
                    }

                    current ??= EnsureImplicitGroup(groups);
                    current.Rules.Add(new RobotsRule(Allow: field == "allow", PathPattern: value));
                    lastLineWasRule = true;
                    break;

                case "crawl-delay":
                    if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delay) && delay > 0 && !double.IsInfinity(delay))
                    {
                        current ??= EnsureImplicitGroup(groups);
                        current.CrawlDelaySeconds = delay;
                    }

                    lastLineWasRule = true;
                    break;

                case "sitemap":
                    if (value.Length > 0)
                    {
                        sitemaps.Add(value);
                    }

                    break;

                default:
                    // Unknown fields are ignored tolerantly.
                    break;
            }
        }

        var built = groups
            .Select(g => new RobotsGroup(g.UserAgents.AsReadOnly(), g.Rules.AsReadOnly(), g.CrawlDelaySeconds))
            .ToList();
        return new RobotsDocument(built.AsReadOnly(), sitemaps.AsReadOnly());
    }

    /// <inheritdoc/>
    public RobotsVerdict Evaluate(RobotsDocument document, string url, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(userAgent);

        var group = SelectGroup(document, userAgent);
        if (group is null)
        {
            return new RobotsVerdict(Allowed: true, MatchedRule: null);
        }

        var path = ExtractPath(url);
        RobotsRule? best = null;
        foreach (var rule in group.Rules)
        {
            if (!PatternMatches(rule.PathPattern, path))
            {
                continue;
            }

            if (best is null
                || rule.PathPattern.Length > best.PathPattern.Length
                || (rule.PathPattern.Length == best.PathPattern.Length && rule.Allow && !best.Allow))
            {
                best = rule;
            }
        }

        return best is null
            ? new RobotsVerdict(Allowed: true, MatchedRule: null)
            : new RobotsVerdict(Allowed: best.Allow, MatchedRule: best);
    }

    /// <summary>
    /// Selects the most specific user-agent group for the crawler identity:
    /// the longest case-insensitive token match wins; <c>*</c> is the wildcard
    /// fallback; ties resolve to the first group in document order.
    /// </summary>
    /// <param name="document">Parsed robots document.</param>
    /// <param name="userAgent">Crawler user-agent token or full product string.</param>
    /// <returns>The winning group, or null when no group applies.</returns>
    public static RobotsGroup? SelectGroup(RobotsDocument document, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(userAgent);

        RobotsGroup? best = null;
        var bestSpecificity = -1;
        foreach (var group in document.Groups)
        {
            var specificity = GroupSpecificity(group, userAgent);
            if (specificity > bestSpecificity)
            {
                best = group;
                bestSpecificity = specificity;
            }
        }

        return best;
    }

    private static int GroupSpecificity(RobotsGroup group, string userAgent)
    {
        var specificity = -1;
        foreach (var agent in group.UserAgents)
        {
            if (agent == "*")
            {
                specificity = Math.Max(specificity, 0);
            }
            else if (userAgent.StartsWith(agent, StringComparison.OrdinalIgnoreCase)
                || agent.StartsWith(userAgent, StringComparison.OrdinalIgnoreCase))
            {
                specificity = Math.Max(specificity, agent.Length);
            }
        }

        return specificity;
    }

    private static string ExtractPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.PathAndQuery;
        }

        return url.StartsWith('/') ? url : "/" + url;
    }

    private static bool PatternMatches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var p = anchored ? pattern[..^1] : pattern;

        var pi = 0;
        var si = 0;
        var starPattern = -1;
        var starPath = 0;

        while (si < path.Length)
        {
            if (pi >= p.Length)
            {
                // Pattern exhausted mid-path: prefix match succeeds unless end-anchored.
                return !anchored;
            }

            if (p[pi] == path[si])
            {
                pi++;
                si++;
            }
            else if (p[pi] == '*')
            {
                starPattern = pi++;
                starPath = si;
            }
            else if (starPattern >= 0)
            {
                pi = starPattern + 1;
                si = ++starPath;
            }
            else
            {
                return false;
            }
        }

        while (pi < p.Length && p[pi] == '*')
        {
            pi++;
        }

        if (pi < p.Length)
        {
            return false;
        }

        return !anchored || si == path.Length;
    }

    private static string StripComment(string line)
    {
        var comment = line.IndexOf('#', StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static GroupBuilder EnsureImplicitGroup(List<GroupBuilder> groups)
    {
        var implicitGroup = new GroupBuilder();
        implicitGroup.UserAgents.Add("*");
        groups.Add(implicitGroup);
        return implicitGroup;
    }

    private sealed class GroupBuilder
    {
        public List<string> UserAgents { get; } = [];

        public List<RobotsRule> Rules { get; } = [];

        public double? CrawlDelaySeconds { get; set; }
    }
}
