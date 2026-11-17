using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite.Extensions;

/// <summary>
/// Delimited-text capability family (FS-CRAWL-001 §2.4: in-database delimited-text
/// import/export). Registers the scalars <c>split_part</c>, <c>split_count</c>,
/// <c>delimited_to_json</c>, <c>csv_quote</c>, and the <c>csv_agg</c> aggregate
/// (export: RFC 4180-style quoting with comma or custom delimiter). Field ordinals
/// are 1-based; empty fields are preserved.
/// <para>
/// In-database import uses the engine's native <c>json_each</c> table-valued function
/// over <c>delimited_to_json</c>, for example:
/// <c>SELECT key + 1 AS ordinal, value FROM json_each(delimited_to_json('x,y,z', ','))</c>.
/// </para>
/// </summary>
internal static class DelimitedTextFunctions
{
    /// <summary>Registers the delimited-text family on an open connection.</summary>
    /// <param name="connection">The connection to extend.</param>
    public static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.CreateFunction(
            "split_part",
            (string? text, string? delimiter, long index) =>
            {
                if (text is null || delimiter is null)
                {
                    return null;
                }

                if (delimiter.Length == 0)
                {
                    throw new ArgumentException("The delimiter must not be empty.", nameof(delimiter));
                }

                if (index <= 0)
                {
                    return null;
                }

                var fields = text.Split(delimiter, StringSplitOptions.None);
                return index > fields.Length ? null : fields[index - 1];
            },
            isDeterministic: true);

        connection.CreateFunction(
            "split_count",
            (string? text, string? delimiter) =>
            {
                if (text is null || delimiter is null)
                {
                    return null;
                }

                if (delimiter.Length == 0)
                {
                    throw new ArgumentException("The delimiter must not be empty.", nameof(delimiter));
                }

                return (long?)text.Split(delimiter, StringSplitOptions.None).Length;
            },
            isDeterministic: true);

        connection.CreateFunction(
            "delimited_to_json",
            (string? text, string? delimiter) =>
            {
                if (text is null || delimiter is null)
                {
                    return null;
                }

                if (delimiter.Length == 0)
                {
                    throw new ArgumentException("The delimiter must not be empty.", nameof(delimiter));
                }

                var fields = text.Split(delimiter, StringSplitOptions.None);
                return JsonSerializer.Serialize(fields);
            },
            isDeterministic: true);

        connection.CreateFunction(
            "csv_quote",
            (string? value) => value is null ? null : QuoteField(value, ","),
            isDeterministic: true);

        connection.CreateAggregate(
            "csv_agg",
            default(CsvAccumulator),
            static (CsvAccumulator acc, string? value) => AppendField(acc, value),
            static (CsvAccumulator acc) => acc.Builder?.ToString() ?? string.Empty,
            isDeterministic: true);

        connection.CreateAggregate(
            "csv_agg",
            default(CsvAccumulator),
            static (CsvAccumulator acc, string? value, string? delimiter) =>
            {
                if (delimiter is not null && !acc.DelimiterFixed)
                {
                    if (delimiter.Length == 0)
                    {
                        throw new ArgumentException("The delimiter must not be empty.", nameof(delimiter));
                    }

                    acc.Delimiter = delimiter;
                    acc.DelimiterFixed = true;
                }

                return AppendField(acc, value);
            },
            static (CsvAccumulator acc) => acc.Builder?.ToString() ?? string.Empty,
            isDeterministic: true);
    }

    private static CsvAccumulator AppendField(CsvAccumulator acc, string? value)
    {
        acc.Builder ??= new StringBuilder();
        var delimiter = acc.Delimiter ?? ",";

        if (acc.HasFields)
        {
            acc.Builder.Append(delimiter);
        }

        // SQL NULL aggregates as an empty unquoted field so column positions stay stable.
        if (value is not null)
        {
            acc.Builder.Append(QuoteField(value, delimiter));
        }

        acc.HasFields = true;
        return acc;
    }

    private static string QuoteField(string value, string delimiter)
    {
        var mustQuote = value.Contains('"')
            || value.Contains('\n')
            || value.Contains('\r')
            || value.Contains(delimiter, StringComparison.Ordinal);

        if (!mustQuote)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var ch in value)
        {
            if (ch == '"')
            {
                builder.Append('"');
            }

            builder.Append(ch);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private struct CsvAccumulator
    {
        public StringBuilder? Builder;

        public string? Delimiter;

        public bool DelimiterFixed;

        public bool HasFields;
    }
}
