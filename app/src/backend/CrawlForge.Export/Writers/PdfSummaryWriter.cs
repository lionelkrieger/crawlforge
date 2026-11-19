using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CrawlForge.Export.Writers;

/// <summary>
/// Optional PDF summary export (FS-CRAWL-001 §8.4). Renders dataset tables as
/// a paginated local PDF with the system font resolver — fully offline. The
/// PDF is a summary surface: each dataset is capped at a bounded row count
/// with an explicit truncation note (tabular exports carry the full data).
/// </summary>
public static class PdfSummaryWriter
{
    /// <summary>Maximum rows rendered per dataset in the PDF summary.</summary>
    public const int MaxRowsPerDataset = 200;

    private const double Margin = 48;
    private const double LineHeight = 14;

    /// <summary>
    /// Writes the PDF summary to the output stream (left open).
    /// </summary>
    /// <param name="output">Destination stream (local .pdf file).</param>
    /// <param name="auditId">Exported audit identifier.</param>
    /// <param name="datasets">Streaming datasets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the PDF is written.</returns>
    public static async Task WriteAsync(Stream output, long auditId, IAsyncEnumerable<ExportDataset> datasets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(datasets);

        LocalFontResolver.EnsureRegistered();
        using var document = new PdfDocument();
        // WP-09-10: uncompressed content streams keep the report text
        // extractable/verifiable (parse-back conformance) at a modest size
        // cost — the PDF is a bounded summary surface (≤200 rows/dataset).
        document.Options.CompressContentStreams = false;
        document.Info.Title = $"CrawlForge Report — Audit {auditId}";
        document.Info.Creator = "CrawlForge (local export)";

        var titleFont = new XFont("Arial", 16, XFontStyleEx.Bold);
        var headerFont = new XFont("Arial", 11, XFontStyleEx.Bold);
        var bodyFont = new XFont("Arial", 9, XFontStyleEx.Regular);

        var page = document.AddPage();
        var graphics = XGraphics.FromPdfPage(page);
        var y = Margin;
        var width = page.Width.Point - (2 * Margin);

        void EnsureSpace(double needed)
        {
            if (y + needed <= page.Height.Point - Margin)
            {
                return;
            }

            graphics.Dispose();
            page = document.AddPage();
            graphics = XGraphics.FromPdfPage(page);
            y = Margin;
        }

        void DrawWrapped(string text, XFont font, XBrush brush)
        {
            var words = text.Split(' ');
            var line = string.Empty;
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (graphics.MeasureString(candidate, font).Width > width && line.Length > 0)
                {
                    EnsureSpace(LineHeight);
                    graphics.DrawString(line, font, brush, new XRect(Margin, y, width, LineHeight), XStringFormats.TopLeft);
                    y += LineHeight;
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            if (line.Length > 0)
            {
                EnsureSpace(LineHeight);
                graphics.DrawString(line, font, brush, new XRect(Margin, y, width, LineHeight), XStringFormats.TopLeft);
                y += LineHeight;
            }
        }

        DrawWrapped($"CrawlForge Report — Audit {auditId}", titleFont, XBrushes.Black);
        DrawWrapped($"Generated locally {DateTimeOffset.UtcNow:O}. Summary surface; full data in spreadsheet/CSV exports.", bodyFont, XBrushes.Gray);
        y += 8;

        await foreach (var dataset in datasets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSpace(LineHeight * 3);
            y += 6;
            DrawWrapped(dataset.Key, headerFont, XBrushes.Black);
            DrawWrapped(string.Join(" | ", dataset.Columns.Select(c => c.Label)), bodyFont, XBrushes.Gray);

            // WP-09-10 (CAP-H-177): the findings section carries a severity
            // rollup so the PDF report summarizes issue pressure at a glance.
            var severityIndex = -1;
            var severityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (dataset.Key == "findings")
            {
                for (var i = 0; i < dataset.Columns.Count; i++)
                {
                    if (dataset.Columns[i].Key == "severity")
                    {
                        severityIndex = i;
                        break;
                    }
                }
            }

            var rows = 0;
            await foreach (var row in dataset.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (rows >= MaxRowsPerDataset)
                {
                    DrawWrapped($"… truncated at {MaxRowsPerDataset} rows (see spreadsheet/CSV export for the full dataset).", bodyFont, XBrushes.Gray);
                    break;
                }

                if (severityIndex >= 0 && severityIndex < row.Values.Count && row.Values[severityIndex] is { } severity)
                {
                    severityCounts[severity] = severityCounts.TryGetValue(severity, out var count) ? count + 1 : 1;
                }

                DrawWrapped(string.Join(" | ", row.Values.Select(v => v ?? string.Empty)), bodyFont, XBrushes.Black);
                rows++;
            }

            if (severityIndex >= 0)
            {
                var rollup = severityCounts.Count == 0
                    ? "Findings rendered: 0."
                    : "Findings rendered: " + rows + " — " + string.Join(", ", severityCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}")) + ".";
                DrawWrapped(rollup, bodyFont, XBrushes.Gray);
            }
        }

        graphics.Dispose();
        document.Save(output, closeStream: false);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
