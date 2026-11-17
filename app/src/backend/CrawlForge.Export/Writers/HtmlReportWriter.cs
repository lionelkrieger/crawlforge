using System.Net;
using System.Text;

namespace CrawlForge.Export.Writers;

/// <summary>
/// Self-contained HTML report bundle (FS-CRAWL-001 §8.4): a single local HTML
/// file with inline CSS and no external assets, scripts, fonts, or network
/// references — it renders fully offline. Datasets stream row-at-a-time into
/// tables (bounded memory at 500k+ rows). An optional template file
/// <c>assets/export-templates/report-template.html</c> overrides the built-in
/// chrome; it must contain the <c>{{CONTENT}}</c> placeholder.
/// </summary>
public static class HtmlReportWriter
{
    /// <summary>Placeholder replaced by the generated report content.</summary>
    public const string ContentPlaceholder = "{{CONTENT}}";

    /// <summary>Built-in offline template (inline CSS only).</summary>
    public const string DefaultTemplate = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>CrawlForge Report</title>
        <style>
        body{font-family:Segoe UI,Arial,sans-serif;margin:2rem;color:#1c2430;background:#fff}
        h1{font-size:1.5rem}h2{font-size:1.15rem;margin-top:2rem;border-bottom:2px solid #e3e8ef;padding-bottom:.3rem}
        table{border-collapse:collapse;width:100%;font-size:.85rem;margin-top:.6rem}
        th,td{border:1px solid #d7dee8;padding:.3rem .5rem;text-align:left;vertical-align:top;word-break:break-word}
        th{background:#f2f5f9;position:sticky;top:0}
        tr:nth-child(even) td{background:#fafbfd}
        .meta{color:#5a6675;font-size:.85rem}
        </style>
        </head>
        <body>
        {{CONTENT}}
        </body>
        </html>
        """;

    /// <summary>
    /// Writes the report. The template is resolved from the optional asset
    /// directory; the built-in template is the offline fallback.
    /// </summary>
    /// <param name="output">Destination stream (local file).</param>
    /// <param name="auditId">Exported audit identifier.</param>
    /// <param name="datasets">Streaming datasets.</param>
    /// <param name="templateDirectory">Optional asset directory containing report-template.html.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the report is written.</returns>
    public static async Task WriteAsync(
        Stream output,
        long auditId,
        IAsyncEnumerable<ExportDataset> datasets,
        string? templateDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(datasets);

        var template = LoadTemplate(templateDirectory);
        var parts = template.Split(ContentPlaceholder, StringSplitOptions.None);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(parts[0].AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync($"<h1>CrawlForge Report — Audit {auditId}</h1>\n<p class=\"meta\">Generated locally {DateTimeOffset.UtcNow:O}. Self-contained: no external resources.</p>\n".AsMemory(), cancellationToken).ConfigureAwait(false);

        await foreach (var dataset in datasets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync($"<h2>{Escape(dataset.Key)}</h2>\n<table>\n<thead>\n<tr>".AsMemory(), cancellationToken).ConfigureAwait(false);
            foreach (var column in dataset.Columns)
            {
                await writer.WriteAsync($"<th>{Escape(column.Label)}</th>".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteAsync("</tr>\n</thead>\n<tbody>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await foreach (var row in dataset.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync("<tr>".AsMemory(), cancellationToken).ConfigureAwait(false);
                foreach (var value in row.Values)
                {
                    await writer.WriteAsync($"<td>{Escape(value ?? string.Empty)}</td>".AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.WriteAsync("</tr>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteAsync("</tbody>\n</table>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        if (parts.Length > 1)
        {
            await writer.WriteAsync(parts[1].AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>HTML-escapes a cell value (offline safety: no markup injection).</summary>
    /// <param name="value">Raw value.</param>
    /// <returns>Escaped text safe for HTML body content.</returns>
    public static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string LoadTemplate(string? templateDirectory)
    {
        if (templateDirectory is not null)
        {
            var path = Path.Combine(templateDirectory, "report-template.html");
            if (File.Exists(path))
            {
                var custom = File.ReadAllText(path);
                if (custom.Contains(ContentPlaceholder, StringComparison.Ordinal))
                {
                    return custom;
                }
            }
        }

        return DefaultTemplate;
    }
}
