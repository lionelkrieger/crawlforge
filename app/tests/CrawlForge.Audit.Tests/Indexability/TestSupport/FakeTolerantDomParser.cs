using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Tests.Indexability.TestSupport;

/// <summary>
/// Test double for the locked ITolerantDomParser contract (the real tolerant
/// parser is built by the parallel pipeline workpiece). Handles the
/// well-formed subset used by indexability tests: title, meta, link, a,
/// script, and paragraph text. Never throws on malformed input.
/// </summary>
internal sealed class FakeTolerantDomParser : ITolerantDomParser
{
    private static readonly Regex MetaPattern = new(
        "<meta\\s+[^>]*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkPattern = new(
        "<link\\s+[^>]*?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitlePattern = new(
        "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AnchorPattern = new(
        "<a\\s+[^>]*?href\\s*=\\s*\"([^\"]*)\"[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ScriptPattern = new(
        "<script\\s+([^>]*)>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParagraphPattern = new(
        "<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AttributePattern = new(
        "([a-zA-Z-]+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);

    public DomDocument Parse(string html, string url)
    {
        var head = new List<DomNode>();
        var body = new List<DomNode>();
        var warnings = new List<string>();

        Match title = TitlePattern.Match(html);
        if (title.Success)
        {
            head.Add(DomBuilder.Text("title", title.Groups[1].Value));
        }

        foreach (Match meta in MetaPattern.Matches(html))
        {
            head.Add(DomBuilder.Element("meta", ParseAttributes(meta.Value)));
        }

        foreach (Match link in LinkPattern.Matches(html))
        {
            head.Add(DomBuilder.Element("link", ParseAttributes(link.Value)));
        }

        foreach (Match paragraph in ParagraphPattern.Matches(html))
        {
            body.Add(DomBuilder.Text("p", paragraph.Groups[1].Value));
        }

        foreach (Match anchor in AnchorPattern.Matches(html))
        {
            body.Add(new DomNode(
                "a",
                new Dictionary<string, string> { ["href"] = anchor.Groups[1].Value },
                Array.Empty<DomNode>(),
                anchor.Groups[2].Value));
        }

        foreach (Match script in ScriptPattern.Matches(html))
        {
            body.Add(new DomNode(
                "script",
                ParseAttributes(script.Groups[1].Value),
                Array.Empty<DomNode>(),
                script.Groups[2].Value));
        }

        if (html.Contains('<') && !html.Contains('>'))
        {
            warnings.Add("malformed-markup");
        }

        DomNode root = DomBuilder.Page(null, head, body);
        return new DomDocument(url, root, warnings);
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributePattern.Matches(tag))
        {
            attributes[attribute.Groups[1].Value.ToLowerInvariant()] = attribute.Groups[2].Value;
        }

        return attributes;
    }
}