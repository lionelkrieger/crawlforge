using System.Text.Json;

namespace CrawlForge.Api.Controllers;

/// <summary>Side-by-side difference of one content field.</summary>
/// <param name="Before">Value in audit A.</param>
/// <param name="After">Value in audit B.</param>
/// <param name="Changed">Whether the values differ.</param>
public sealed record ContentDiffWire(string? Before, string? After, bool Changed);

/// <summary>One metadata field change between two audits.</summary>
/// <param name="Field">Metadata field name.</param>
/// <param name="Before">Value in audit A.</param>
/// <param name="After">Value in audit B.</param>
public sealed record MetadataChangeWire(string Field, string? Before, string? After);

/// <summary>Content comparison response.</summary>
/// <param name="CanonicalUrl">Compared canonical URL.</param>
/// <param name="Title">Title difference.</param>
/// <param name="Headings">Per-heading differences.</param>
/// <param name="Text">Text-volume difference.</param>
/// <param name="MetadataChanges">Changed metadata fields.</param>
public sealed record ContentCompareResponseWire(
    string CanonicalUrl,
    ContentDiffWire Title,
    IReadOnlyList<ContentDiffWire> Headings,
    ContentDiffWire Text,
    IReadOnlyList<MetadataChangeWire> MetadataChanges);

/// <summary>One dynamic-column extraction row.</summary>
/// <param name="UrlId">URL identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="RuleKey">Capturing rule name.</param>
/// <param name="Values">Column name to captured value(s).</param>
public sealed record ExtractedContentRowWire(string UrlId, string CanonicalUrl, string RuleKey, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>Extraction response (columns + rows).</summary>
/// <param name="Columns">Effective column order.</param>
/// <param name="Rows">Extraction rows.</param>
public sealed record ExtractedContentResponseWire(IReadOnlyList<string> Columns, IReadOnlyList<ExtractedContentRowWire> Rows);

/// <summary>Per-rule extraction summary.</summary>
/// <param name="RuleKey">Rule name.</param>
/// <param name="PagesMatched">Distinct pages matched.</param>
/// <param name="ValuesCaptured">Valid values captured.</param>
/// <param name="Discards">Invalid values discarded.</param>
/// <param name="Truncations">Values truncated at a bound.</param>
public sealed record ExtractionRuleSummaryWire(string RuleKey, long PagesMatched, long ValuesCaptured, long Discards, long Truncations);

/// <summary>Extraction summary response.</summary>
/// <param name="Rules">Per-rule summaries.</param>
public sealed record ExtractionSummaryResponseWire(IReadOnlyList<ExtractionRuleSummaryWire> Rules);

/// <summary>Per-item custom search overview.</summary>
/// <param name="Item">Search item key.</param>
/// <param name="MatchCount">Total matches.</param>
/// <param name="UrlCount">Distinct URLs.</param>
public sealed record SearchItemOverviewWire(string Item, long MatchCount, long UrlCount);

/// <summary>One custom search match.</summary>
/// <param name="UrlId">URL identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="Item">Search item key.</param>
/// <param name="Snippet">Matched snippet, when present.</param>
/// <param name="Occurrences">Match count on the page.</param>
public sealed record SearchMatchWire(string UrlId, string CanonicalUrl, string Item, string? Snippet, long Occurrences);

/// <summary>Custom search response (overview + matches).</summary>
/// <param name="Overview">Per-item overview.</param>
/// <param name="Matches">Search matches.</param>
public sealed record SearchResultsResponseWire(IReadOnlyList<SearchItemOverviewWire> Overview, IReadOnlyList<SearchMatchWire> Matches);

/// <summary>One dynamic-column snippet output row.</summary>
/// <param name="UrlId">URL identifier.</param>
/// <param name="CanonicalUrl">Canonical URL.</param>
/// <param name="SnippetKey">Snippet key.</param>
/// <param name="Values">Column name to value.</param>
public sealed record SnippetResultRowWire(string UrlId, string CanonicalUrl, string SnippetKey, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>Snippet response (columns + rows).</summary>
/// <param name="Columns">Effective column order.</param>
/// <param name="Rows">Snippet rows.</param>
public sealed record SnippetResultsResponseWire(IReadOnlyList<string> Columns, IReadOnlyList<SnippetResultRowWire> Rows);

/// <summary>One technology detection.</summary>
/// <param name="Name">Technology key/name.</param>
/// <param name="Category">Technology category.</param>
/// <param name="Confidence">Detection confidence in [0,1].</param>
/// <param name="Version">Version, when known.</param>
/// <param name="UrlId">URL reference, when per-URL.</param>
/// <param name="Evidence">Evidence detail, when present.</param>
public sealed record TechnologyDetectionWire(string Name, string Category, double Confidence, string? Version, string? UrlId, string? Evidence);
