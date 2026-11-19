using CrawlForge.Contracts.Enums;

namespace CrawlForge.Export;

/// <summary>
/// Additive export format wire keys (WP-09-10: CAP-H-175 JSON, CAP-G-164 full
/// page text, CAP-G-165 full page Markdown; FS-CRAWL-001 §8.4 CR-011/CR-017,
/// §11.8 agent data contract). The locked <see cref="ExportFormat"/> enum
/// (Contracts.Enums, read-only for builders) carries only the five baseline
/// formats; these additive keys travel as strings through the additive engine
/// surface until the coordinator appends Json/Text/Markdown enum members
/// (handoff: the existing <c>ToWireValue</c> fallback already lowers new member
/// names, so the amendment is wire-compatible by construction).
/// </summary>
public static class AdditiveExportFormats
{
    /// <summary>Summary-first compact JSON document (agent contract, §11.8).</summary>
    public const string Json = "json";

    /// <summary>JSON Lines bulk dataset file — one JSON object per row (§11.8).</summary>
    public const string JsonLines = "jsonl";

    /// <summary>Full page content as plain text, one file per page (§8.4 CR-017).</summary>
    public const string Text = "text";

    /// <summary>Full page content as Markdown, one file per page (§8.4 CR-017).</summary>
    public const string Markdown = "markdown";

    /// <summary>All additive wire keys.</summary>
    public static IReadOnlyList<string> Known { get; } = [Json, JsonLines, Text, Markdown];

    /// <summary>True when the wire key is an additive format.</summary>
    /// <param name="formatWire">Wire format key.</param>
    /// <returns>True for json/jsonl/text/markdown.</returns>
    public static bool IsKnown(string? formatWire)
        => formatWire is Json or JsonLines or Text or Markdown;

    /// <summary>File extension for an additive format's local artifact.</summary>
    /// <param name="formatWire">Additive wire key.</param>
    /// <returns>The artifact extension (zip for per-page bundles).</returns>
    /// <exception cref="InvalidOperationException">Unknown additive key.</exception>
    public static string ExtensionFor(string formatWire) => formatWire switch
    {
        Json => ".json",
        JsonLines => ".jsonl",
        Text => ".zip",
        Markdown => ".zip",
        _ => throw new InvalidOperationException($"Unknown additive export format '{formatWire}'."),
    };
}

/// <summary>
/// Additive export job projection (WP-09-10). Mirrors the locked
/// <c>ExportJobDto</c> with a string wire format so additive formats are
/// represented honestly while the locked enum amendment is pending. The
/// locked <c>GetExportAsync</c> surface returns null for additive jobs;
/// consumers use <c>ExportEngine.GetAdditiveExportAsync</c> (or the unified
/// wire projection) instead.
/// </summary>
/// <param name="Id">Export job identifier.</param>
/// <param name="AuditId">Exported audit identifier.</param>
/// <param name="Format">Wire format key (json, jsonl, text, markdown, or a baseline key).</param>
/// <param name="Status">Job lifecycle status.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="FilePath">Local file path when completed.</param>
/// <param name="Datasets">Selected dataset keys.</param>
public sealed record AdditiveExportJobDto(
    long Id,
    long AuditId,
    string Format,
    ExportJobStatus Status,
    DateTimeOffset CreatedAt,
    string? FilePath,
    IReadOnlyList<string> Datasets);