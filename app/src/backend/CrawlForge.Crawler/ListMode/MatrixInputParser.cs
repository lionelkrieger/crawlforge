namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Parses the two-column migration matrix — <c>Source_URL</c>,
/// <c>Expected_Target_URL</c> — from .csv, .txt, or clipboard text
/// (FS-CRAWL-002 §10.3). Header detection is automatic and case-insensitive;
/// without a header the first two columns are positional. Both URLs of every
/// row are validated as absolute http(s); malformed rows are rejected into a
/// bounded diagnostic list and never fail the upload. Parsing is purely local.
/// </summary>
public static class MatrixInputParser
{
    /// <summary>Maximum retained per-line diagnostics; further rejections are counted only.</summary>
    public const int MaxDiagnostics = 100;

    /// <summary>Header token of the source column.</summary>
    public const string SourceHeader = "Source_URL";

    /// <summary>Header token of the expected-target column.</summary>
    public const string TargetHeader = "Expected_Target_URL";

    /// <summary>
    /// Parses matrix text (CSV with comma/tab delimiters, or whitespace-separated
    /// two-column text). Column names may override the header/positional defaults.
    /// </summary>
    /// <param name="text">Raw matrix text.</param>
    /// <param name="sourceColumn">Optional source column header name or zero-based index.</param>
    /// <param name="targetColumn">Optional target column header name or zero-based index.</param>
    /// <returns>Accepted rows in ordinal order plus bounded diagnostics.</returns>
    public static MatrixParseResult Parse(string text, string? sourceColumn = null, string? targetColumn = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var rows = new List<(int LineNumber, string[] Cells)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            rows.Add((i + 1, SplitCells(lines[i])));
        }

        if (rows.Count == 0)
        {
            return new MatrixParseResult([], [], 0);
        }

        var headerCells = rows[0].Cells;
        var hasHeader = DetectHeader(headerCells);
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows;
        var sourceIndex = ResolveColumn(headerCells, hasHeader, sourceColumn, defaultIndex: 0);
        var targetIndex = ResolveColumn(headerCells, hasHeader, targetColumn, defaultIndex: 1);

        var accepted = new List<MatrixInputRow>();
        var diagnostics = new List<ListLineDiagnostic>();
        var invalidCount = 0;
        var ordinal = 0;

        foreach (var (lineNumber, cells) in dataRows)
        {
            var source = sourceIndex < cells.Length ? cells[sourceIndex].Trim() : string.Empty;
            var target = targetIndex < cells.Length ? cells[targetIndex].Trim() : string.Empty;

            if (!ListInputParser.TryValidateUrl(source, out var sourceReason))
            {
                invalidCount++;
                AddDiagnostic(diagnostics, lineNumber, string.Join(",", cells), $"source: {sourceReason ?? "invalid URL"}");
                continue;
            }

            if (!ListInputParser.TryValidateUrl(target, out var targetReason))
            {
                invalidCount++;
                AddDiagnostic(diagnostics, lineNumber, string.Join(",", cells), $"target: {targetReason ?? "invalid URL"}");
                continue;
            }

            accepted.Add(new MatrixInputRow(ordinal, source, target));
            ordinal++;
        }

        return new MatrixParseResult(accepted, diagnostics, invalidCount);
    }

    private static string[] SplitCells(string line)
    {
        if (line.Contains(',') || line.Contains('\t'))
        {
            var delimiter = line.Contains('\t') ? '\t' : ',';
            return line.Split(delimiter).Select(cell => cell.Trim().Trim('"')).ToArray();
        }

        // Whitespace-separated two-column text: URLs never contain spaces.
        return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool DetectHeader(string[] cells)
    {
        if (cells.Any(cell => ListInputParser.TryValidateUrl(cell, out _)))
        {
            return false;
        }

        return cells.Any(cell =>
            string.Equals(cell, SourceHeader, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cell, TargetHeader, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveColumn(string[] headerCells, bool hasHeader, string? column, int defaultIndex)
    {
        if (column is null)
        {
            if (!hasHeader)
            {
                return defaultIndex;
            }

            var wanted = defaultIndex == 0 ? SourceHeader : TargetHeader;
            for (var c = 0; c < headerCells.Length; c++)
            {
                if (string.Equals(headerCells[c], wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            return defaultIndex;
        }

        if (int.TryParse(column, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
            index >= 0)
        {
            return index;
        }

        if (hasHeader)
        {
            for (var c = 0; c < headerCells.Length; c++)
            {
                if (string.Equals(headerCells[c], column, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }
        }

        throw new ArgumentException($"Matrix column '{column}' was not found in the header.", nameof(column));
    }

    private static void AddDiagnostic(List<ListLineDiagnostic> diagnostics, int lineNumber, string raw, string reason)
    {
        if (diagnostics.Count < MaxDiagnostics)
        {
            diagnostics.Add(new ListLineDiagnostic(lineNumber, raw, reason));
        }
    }
}
