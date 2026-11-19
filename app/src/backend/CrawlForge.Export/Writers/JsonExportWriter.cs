using System.Globalization;
using System.Text.Json;

namespace CrawlForge.Export.Writers;

/// <summary>
/// JSON export writer (WP-09-10, CAP-H-175; agent data contract FS-CRAWL-001
/// §11.8 / FS-CRAWL-002 Appendix D). Two bulk shapes, both streaming with
/// bounded memory (FS-CRAWL-002 §7.1 — datasets are forward-only and never
/// materialized):
/// <list type="bullet">
/// <item><see cref="WriteAsync"/> — one compact JSON document, summary-first:
/// audit identity and per-dataset column schema lead, rows follow, and each
/// dataset closes with its exact row count.</item>
/// <item><see cref="WriteLinesAsync"/> — JSON Lines: one JSON object per row
/// (the §11.8 bulk shape for nested records), each line tagged with its
/// dataset key.</item>
/// </list>
/// Every row object is keyed by the dataset column keys; when the reader runs
/// in identifier mode the rows carry the same stable identifiers as the API
/// (url_id, finding_id, link_id, …). Cell values are typed by the column
/// ValueType (number/boolean parse invariantly; everything else is a string;
/// null stays null) — writers never re-parse payloads beyond that typing.
/// </summary>
public static class JsonExportWriter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// Writes the summary-first compact JSON document to the output stream
    /// (left open).
    /// </summary>
    /// <param name="output">Destination stream (local .json file).</param>
    /// <param name="auditId">Exported audit identifier.</param>
    /// <param name="datasets">Streaming datasets (identifier mode recommended).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the document is written.</returns>
    public static async Task WriteAsync(
        Stream output,
        long auditId,
        IAsyncEnumerable<ExportDataset> datasets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(datasets);

        await using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("auditId", auditId.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("generatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteStartArray("datasets");

        await foreach (var dataset in datasets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("key", dataset.Key);
            WriteColumns(writer, dataset.Columns);

            writer.WriteStartArray("rows");
            var rowCount = 0L;
            await foreach (var row in dataset.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                WriteRowObject(writer, dataset.Columns, row);
                rowCount++;
                if (rowCount % 256 == 0)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            writer.WriteEndArray(); // rows
            writer.WriteNumber("rowCount", rowCount);
            writer.WriteEndObject(); // dataset
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        writer.WriteEndArray(); // datasets
        writer.WriteEndObject(); // root
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes JSON Lines: one JSON object per row, tagged with the dataset key
    /// (the §11.8 bulk shape for agents working in code). The stream is left
    /// open; every line is newline-terminated UTF-8 without a BOM.
    /// </summary>
    /// <param name="output">Destination stream (local .jsonl file).</param>
    /// <param name="datasets">Streaming datasets (identifier mode recommended).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when every line is written.</returns>
    public static async Task WriteLinesAsync(
        Stream output,
        IAsyncEnumerable<ExportDataset> datasets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(datasets);

        await foreach (var dataset in datasets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var row in dataset.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await using var writer = new Utf8JsonWriter(output, WriterOptions);
                writer.WriteStartObject();
                writer.WriteString("dataset", dataset.Key);
                WriteRowFields(writer, dataset.Columns, row);
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.WriteByte((byte)'\n');
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one dataset as a summary-first JSON document fragment (test and
    /// composition surface): <c>{"key","columns","rows","rowCount"}</c>.
    /// </summary>
    /// <param name="output">Destination stream.</param>
    /// <param name="dataset">Dataset to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public static async Task<long> WriteDatasetAsync(Stream output, ExportDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(dataset);

        await using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("key", dataset.Key);
        WriteColumns(writer, dataset.Columns);
        writer.WriteStartArray("rows");
        var rowCount = 0L;
        await foreach (var row in dataset.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            WriteRowObject(writer, dataset.Columns, row);
            rowCount++;
        }

        writer.WriteEndArray();
        writer.WriteNumber("rowCount", rowCount);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return rowCount;
    }

    private static void WriteColumns(Utf8JsonWriter writer, IReadOnlyList<ExportColumn> columns)
    {
        writer.WriteStartArray("columns");
        foreach (var column in columns)
        {
            writer.WriteStartObject();
            writer.WriteString("key", column.Key);
            writer.WriteString("label", column.Label);
            writer.WriteString("valueType", column.ValueType);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRowObject(Utf8JsonWriter writer, IReadOnlyList<ExportColumn> columns, ExportRow row)
    {
        writer.WriteStartObject();
        WriteRowFields(writer, columns, row);
        writer.WriteEndObject();
    }

    private static void WriteRowFields(Utf8JsonWriter writer, IReadOnlyList<ExportColumn> columns, ExportRow row)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            var value = i < row.Values.Count ? row.Values[i] : null;
            writer.WritePropertyName(columns[i].Key);
            WriteTypedValue(writer, columns[i].ValueType, value);
        }
    }

    private static void WriteTypedValue(Utf8JsonWriter writer, string valueType, string? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (valueType)
        {
            case "number" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                writer.WriteNumberValue(number);
                break;
            case "boolean" when bool.TryParse(value, out var flag):
                writer.WriteBooleanValue(flag);
                break;
            default:
                writer.WriteStringValue(value);
                break;
        }
    }
}