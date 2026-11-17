using System.Globalization;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Parses user-supplied URL lists from .txt (one URL per line), .csv
/// (configurable URL column, automatic header detection), or clipboard paste
/// (FS-CRAWL-002 §10.2). Parsing is purely local. Malformed entries are
/// rejected into a bounded per-line diagnostic list and never fail the audit.
/// </summary>
public static class ListInputParser
{
    /// <summary>Maximum retained per-line diagnostics; further rejections are counted only.</summary>
    public const int MaxDiagnostics = 100;

    /// <summary>
    /// Parses one-URL-per-line text (.txt or clipboard paste). Blank lines are
    /// ignored silently; every non-blank line is validated as an absolute
    /// http(s) URL.
    /// </summary>
    /// <param name="text">Raw input text.</param>
    /// <returns>Accepted entries plus bounded diagnostics.</returns>
    public static ListParseResult ParseText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<ListInputEntry>();
        var diagnostics = new List<ListLineDiagnostic>();
        var invalidCount = 0;

        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            if (TryValidateUrl(raw, out var reason))
            {
                entries.Add(new ListInputEntry(i + 1, raw));
            }
            else
            {
                invalidCount++;
                AddDiagnostic(diagnostics, i + 1, raw, reason);
            }
        }

        return new ListParseResult(entries, diagnostics, invalidCount);
    }

    /// <summary>
    /// Parses CSV text with automatic header detection and a configurable URL
    /// column. <paramref name="urlColumn"/> may be a header name (matched
    /// case-insensitively) or a zero-based column index; when null, the first
    /// column whose cells validate as URLs is selected (fallback: column 0).
    /// Comma and tab delimiters are supported.
    /// </summary>
    /// <param name="text">Raw CSV text.</param>
    /// <param name="urlColumn">Optional header name or zero-based column index.</param>
    /// <returns>Accepted entries plus bounded diagnostics.</returns>
    public static ListParseResult ParseCsv(string text, string? urlColumn = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = SplitLines(text);
        var rows = new List<(int LineNumber, string[] Cells)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            rows.Add((i + 1, SplitCells(lines[i])));
        }

        if (rows.Count == 0)
        {
            return new ListParseResult([], [], 0);
        }

        var hasHeader = DetectHeader(rows[0].Cells);
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows;
        var columnIndex = ResolveColumnIndex(rows[0].Cells, hasHeader, urlColumn, dataRows);

        var entries = new List<ListInputEntry>();
        var diagnostics = new List<ListLineDiagnostic>();
        var invalidCount = 0;

        foreach (var (lineNumber, cells) in dataRows)
        {
            var raw = columnIndex < cells.Length ? cells[columnIndex].Trim() : string.Empty;
            if (raw.Length == 0)
            {
                invalidCount++;
                AddDiagnostic(diagnostics, lineNumber, string.Empty, "missing URL cell");
                continue;
            }

            if (TryValidateUrl(raw, out var reason))
            {
                entries.Add(new ListInputEntry(lineNumber, raw));
            }
            else
            {
                invalidCount++;
                AddDiagnostic(diagnostics, lineNumber, raw, reason);
            }
        }

        return new ListParseResult(entries, diagnostics, invalidCount);
    }

    /// <summary>
    /// Parses clipboard paste: multi-cell lines are treated as CSV, otherwise
    /// as one-URL-per-line text (FS-CRAWL-002 §10.2 clipboard ingestion).
    /// </summary>
    /// <param name="text">Raw clipboard text.</param>
    /// <param name="urlColumn">Optional CSV header name or zero-based column index.</param>
    /// <returns>Accepted entries plus bounded diagnostics.</returns>
    public static ListParseResult ParseClipboard(string text, string? urlColumn = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = SplitLines(text);
        var looksCsv = lines.Any(line => line.Contains(',') || line.Contains('\t'));
        return looksCsv ? ParseCsv(text, urlColumn) : ParseText(text);
    }

    /// <summary>
    /// Validates one candidate URL: it must be an absolute http(s) URL with a
    /// non-empty host (FS-CRAWL-002 §10.2 per-entry validation).
    /// </summary>
    /// <param name="raw">Candidate URL text.</param>
    /// <param name="reason">Rejection reason when invalid; null when valid.</param>
    /// <returns>True when the candidate is a usable absolute http(s) URL.</returns>
    public static bool TryValidateUrl(string raw, out string? reason)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            reason = "not an absolute URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"scheme '{uri.Scheme}' is not http(s)");
            return false;
        }

        if (uri.Host.Length == 0)
        {
            reason = "missing host";
            return false;
        }

        reason = null;
        return true;
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

    private static string[] SplitCells(string line)
    {
        var delimiter = line.Contains('\t') ? '\t' : ',';
        return line.Split(delimiter).Select(cell => cell.Trim().Trim('"')).ToArray();
    }

    private static bool DetectHeader(string[] cells)
    {
        // A header row carries no valid URL cell and at least one known header token.
        if (cells.Any(cell => TryValidateUrl(cell, out _)))
        {
            return false;
        }

        string[] knownTokens = ["url", "urls", "source_url", "link", "links", "address", "seed", "seeds"];
        return cells.Any(cell => knownTokens.Contains(cell.ToLowerInvariant(), StringComparer.Ordinal));
    }

    private static int ResolveColumnIndex(
        string[] firstRowCells,
        bool hasHeader,
        string? urlColumn,
        List<(int LineNumber, string[] Cells)> dataRows)
    {
        if (urlColumn is not null)
        {
            if (int.TryParse(urlColumn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitIndex) &&
                explicitIndex >= 0)
            {
                return explicitIndex;
            }

            if (hasHeader)
            {
                for (var c = 0; c < firstRowCells.Length; c++)
                {
                    if (string.Equals(firstRowCells[c], urlColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        return c;
                    }
                }
            }

            throw new ArgumentException($"URL column '{urlColumn}' was not found in the CSV header.", nameof(urlColumn));
        }

        // Auto-detect: first column whose data cells mostly validate as URLs.
        var columnCount = dataRows.Count == 0 ? firstRowCells.Length : dataRows.Max(r => r.Cells.Length);
        var bestColumn = 0;
        var bestScore = -1;
        for (var c = 0; c < columnCount; c++)
        {
            var score = dataRows.Count(r => c < r.Cells.Length && TryValidateUrl(r.Cells[c].Trim(), out _));
            if (score > bestScore)
            {
                bestScore = score;
                bestColumn = c;
            }
        }

        return bestColumn;
    }

    private static void AddDiagnostic(List<ListLineDiagnostic> diagnostics, int lineNumber, string raw, string? reason)
    {
        if (diagnostics.Count < MaxDiagnostics)
        {
            diagnostics.Add(new ListLineDiagnostic(lineNumber, raw, reason ?? "invalid URL"));
        }
    }
}
