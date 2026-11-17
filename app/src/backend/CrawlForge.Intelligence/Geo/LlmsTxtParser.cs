using System.Text.RegularExpressions;

namespace CrawlForge.Intelligence.Geo;

/// <summary>One markdown link extracted from an llms.txt document.</summary>
/// <param name="Text">Link text.</param>
/// <param name="Url">Raw URL as written.</param>
/// <param name="ResolvedUrl">Absolute URL resolved against the base, or null when unresolvable.</param>
public sealed record LlmsTxtLink(string Text, string Url, string? ResolvedUrl);

/// <summary>One heading section of an llms.txt document.</summary>
/// <param name="Heading">Heading text.</param>
/// <param name="Level">Heading level (1 = H1, 2 = H2, ...).</param>
/// <param name="Links">Links listed under the heading.</param>
public sealed record LlmsTxtSection(string Heading, int Level, IReadOnlyList<LlmsTxtLink> Links);

/// <summary>
/// Parsed llms.txt document (FS-CRAWL-002 §13.1 community convention: H1
/// title, blockquote summary, H2 sections with markdown link lists).
/// </summary>
/// <param name="Title">H1 title text, when present.</param>
/// <param name="Summary">Blockquote summary text, when present.</param>
/// <param name="Sections">Heading sections in document order.</param>
/// <param name="Links">All links in document order.</param>
public sealed record LlmsTxtDocument(
    string? Title,
    string? Summary,
    IReadOnlyList<LlmsTxtSection> Sections,
    IReadOnlyList<LlmsTxtLink> Links)
{
    /// <summary>Whether an H1 title is present.</summary>
    public bool HasH1 => Title is not null;

    /// <summary>Whether a blockquote summary is present.</summary>
    public bool HasBlockquote => Summary is not null;

    /// <summary>Whether at least one H2 section contains a link list.</summary>
    public bool HasH2SectionsWithLinks => Sections.Any(s => s.Level == 2 && s.Links.Count > 0);
}

/// <summary>
/// Pure parser for llms.txt / llms-full.txt bodies (FS-CRAWL-002 §13.1).
/// llms.txt is markdown-like plain text; parsing is local and bounded
/// (100 ms regex timeout). No network access ever occurs.
/// </summary>
public static class LlmsTxtParser
{
    private static readonly Regex LinkPattern = new(
        @"\[([^\]]*)\]\(([^)\s]+)\)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Parses an llms.txt body, resolving links against an optional base URL.</summary>
    /// <param name="body">Raw llms.txt content.</param>
    /// <param name="baseUrl">Optional base URL for relative link resolution.</param>
    /// <returns>The parsed document.</returns>
    public static LlmsTxtDocument Parse(string body, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(body);
        Uri? baseUri = null;
        if (baseUrl is not null)
        {
            Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);
        }

        string? title = null;
        var summaryLines = new List<string>();
        var sections = new List<LlmsTxtSection>();
        var links = new List<LlmsTxtLink>();
        string? currentHeading = null;
        var currentLevel = 0;
        var sectionLinks = new List<LlmsTxtLink>();

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (currentHeading is not null)
                {
                    sections.Add(new LlmsTxtSection(currentHeading, currentLevel, sectionLinks.ToArray()));
                    sectionLinks = [];
                }

                var level = 0;
                while (level < line.Length && line[level] == '#')
                {
                    level++;
                }

                currentHeading = line[level..].Trim();
                currentLevel = level;
                if (level == 1 && title is null)
                {
                    title = currentHeading;
                }

                continue;
            }

            if (line.StartsWith('>'))
            {
                summaryLines.Add(line[1..].Trim());
                continue;
            }

            foreach (Match match in LinkPattern.Matches(line))
            {
                var text = match.Groups[1].Value;
                var url = match.Groups[2].Value;
                var link = new LlmsTxtLink(text, url, Resolve(baseUri, url));
                links.Add(link);
                sectionLinks.Add(link);
            }
        }

        if (currentHeading is not null)
        {
            sections.Add(new LlmsTxtSection(currentHeading, currentLevel, sectionLinks.ToArray()));
        }

        var summary = summaryLines.Count > 0 ? string.Join(" ", summaryLines.Where(l => l.Length > 0)) : null;
        return new LlmsTxtDocument(
            title,
            string.IsNullOrWhiteSpace(summary) ? null : summary,
            sections,
            links);
    }

    private static string? Resolve(Uri? baseUri, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsoluteUri;
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, url, out var relative))
        {
            return relative.AbsoluteUri;
        }

        return null;
    }
}
