using System.IO.Compression;
using System.Text;

namespace CrawlForge.Export.Writers;

/// <summary>
/// Streaming CSV export (FS-CRAWL-001 §8.4 "CSV per dataset"): one RFC-4180
/// CSV entry per dataset inside a single zip archive, written row-at-a-time
/// with bounded memory (FS-CRAWL-002 §7.1). Dynamic columns appear exactly as
/// the dataset schema declares them.
/// </summary>
public static class CsvExportWriter
{
    /// <summary>
    /// Writes all datasets into a zip archive on the output stream. The stream
    /// is left open; the archive is finalized on completion.
    /// </summary>
    /// <param name="output">Destination stream (local file).</param>
    /// <param name="datasets">Streaming datasets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the archive is written.</returns>
    public static async Task WriteAsync(Stream output, IAsyncEnumerable<ExportDataset> datasets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(datasets);

        await using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await foreach (var dataset in datasets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var entry = archive.CreateEntry(dataset.Key + ".csv", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await WriteDatasetAsync(writer, dataset, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes one dataset as CSV (header + streaming rows) to a text writer.
    /// </summary>
    /// <param name="writer">Destination text writer.</param>
    /// <param name="dataset">Dataset to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the dataset is written.</returns>
    public static async Task WriteDatasetAsync(TextWriter writer, ExportDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(dataset);

        WriteRow(writer, dataset.Columns.Select(c => c.Label));
        await foreach (var row in dataset.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            WriteRow(writer, row.Values);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one RFC-4180 row: fields containing commas, quotes, or newlines
    /// are quoted; embedded quotes are doubled. Null cells are empty fields.
    /// </summary>
    /// <param name="writer">Destination text writer.</param>
    /// <param name="values">Cell values.</param>
    public static void WriteRow(TextWriter writer, IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(values);

        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                writer.Write(',');
            }

            first = false;
            if (value is null)
            {
                continue;
            }

            if (value.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            {
                writer.Write('"');
                writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
                writer.Write('"');
            }
            else
            {
                writer.Write(value);
            }
        }

        writer.Write("\r\n");
    }
}
