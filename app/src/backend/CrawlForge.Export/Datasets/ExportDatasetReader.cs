using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CrawlForge.Audit.Custom;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Export.Datasets;

/// <summary>
/// Streams export datasets from local aggregates (FS-CRAWL-001 §8.4). Every
/// dataset is a forward-only <see cref="IAsyncEnumerable{T}"/> over a database
/// reader: no dataset is materialized in memory, honoring the 500k-row bound
/// (FS-CRAWL-002 §7.1). Dynamic extraction/snippet columns are built through
/// the locked <see cref="DynamicColumnSchema"/> contract with bounded schema
/// passes (one representative per rule / first-seen columns per snippet).
/// </summary>
public sealed class ExportDatasetReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbProvider _provider;

    /// <summary>Creates the reader over the configured database provider.</summary>
    /// <param name="provider">Database provider (locked contract).</param>
    public ExportDatasetReader(IDbProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <summary>
    /// Reads the requested datasets in request order. Unknown keys are skipped
    /// (open dataset vocabulary); an empty selection yields the default sheets.
    /// </summary>
    /// <param name="auditId">Audit to export.</param>
    /// <param name="keys">Requested dataset keys.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Streaming datasets, each enumerable exactly once.</returns>
    public async IAsyncEnumerable<ExportDataset> ReadAsync(
        long auditId,
        IReadOnlyList<string> keys,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var selection = keys.Count == 0 ? ExportDatasetKeys.Default : keys;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(key))
            {
                continue;
            }

            switch (key)
            {
                case ExportDatasetKeys.Summary:
                    yield return new ExportDataset(key, SummaryColumns(), StreamSummaryAsync(auditId, cancellationToken));
                    break;
                case ExportDatasetKeys.Urls:
                    yield return new ExportDataset(key, UrlColumns(), StreamUrlsAsync(auditId, cancellationToken));
                    break;
                case ExportDatasetKeys.Findings:
                    yield return new ExportDataset(key, FindingColumns(), StreamFindingsAsync(auditId, cancellationToken));
                    break;
                case ExportDatasetKeys.Links:
                    yield return new ExportDataset(key, LinkColumns(), StreamLinksAsync(auditId, cancellationToken));
                    break;
                case ExportDatasetKeys.CustomExtraction:
                    var extraction = await BuildExtractionDatasetAsync(auditId, cancellationToken).ConfigureAwait(false);
                    yield return extraction;
                    break;
                case ExportDatasetKeys.Snippets:
                    var snippets = await BuildSnippetDatasetAsync(auditId, cancellationToken).ConfigureAwait(false);
                    yield return snippets;
                    break;
                case ExportDatasetKeys.NGrams:
                    yield return new ExportDataset(key, NGramColumns(), StreamSimpleAsync(auditId, NGramSql, NGramCell, cancellationToken));
                    break;
                case ExportDatasetKeys.RedirectMatrix:
                    yield return new ExportDataset(key, RedirectColumns(), StreamSimpleAsync(auditId, RedirectSql, RedirectCell, cancellationToken));
                    break;
                case ExportDatasetKeys.WafBlocks:
                    yield return new ExportDataset(key, WafColumns(), StreamSimpleAsync(auditId, WafSql, WafCell, cancellationToken));
                    break;
                case ExportDatasetKeys.SemanticOpportunity:
                    yield return new ExportDataset(key, SemanticColumns(), StreamSimpleAsync(auditId, SemanticSql, SemanticCell, cancellationToken));
                    break;
                case ExportDatasetKeys.Analytics:
                    yield return new ExportDataset(key, AnalyticsColumns(), StreamSimpleAsync(auditId, AnalyticsSql, AnalyticsCell, cancellationToken));
                    break;
                default:
                    // Open vocabulary: unknown datasets are skipped, never fatal.
                    break;
            }
        }
    }

    // ---- fixed columns -----------------------------------------------------

    private static IReadOnlyList<ExportColumn> SummaryColumns() =>
    [
        new("section", "Section", "text"),
        new("key", "Key", "text"),
        new("dimensions", "Dimensions", "text"),
        new("value", "Value", "text"),
        new("detail", "Detail", "text"),
    ];

    private static IReadOnlyList<ExportColumn> UrlColumns() =>
    [
        new("url", "URL", "text"),
        new("host", "Host", "text"),
        new("path", "Path", "text"),
        new("depth", "Depth", "number"),
        new("discovery_source", "Discovery Source", "text"),
        new("status_class", "Status Class", "text"),
        new("final_status_code", "Final Status Code", "number"),
        new("redirect_target_url", "Redirect Target", "text"),
        new("content_type", "Content Type", "text"),
        new("size_bytes", "Size (bytes)", "number"),
        new("robots_state", "Robots State", "text"),
        new("indexability_flags", "Indexability Flags", "number"),
        new("is_indexable", "Indexable", "boolean"),
        new("timing_json", "Timing", "text"),
    ];

    private static IReadOnlyList<ExportColumn> FindingColumns() =>
    [
        new("rule_key", "Rule", "text"),
        new("category", "Category", "text"),
        new("severity", "Severity", "text"),
        new("url", "URL", "text"),
        new("evidence_json", "Evidence", "text"),
        new("recommendation_key", "Recommendation", "text"),
        new("is_suppressed", "Suppressed", "boolean"),
    ];

    private static IReadOnlyList<ExportColumn> LinkColumns() =>
    [
        new("source_url", "Source URL", "text"),
        new("canonical_target_url", "Target URL", "text"),
        new("target_url", "Resolved Target", "text"),
        new("anchor_text", "Anchor Text", "text"),
        new("relation_flags", "Relation Flags", "number"),
        new("is_internal", "Internal", "boolean"),
    ];

    private static IReadOnlyList<ExportColumn> NGramColumns() =>
    [
        new("zone", "Zone", "text"),
        new("ngram_order", "Order", "number"),
        new("term", "Term", "text"),
        new("document_frequency", "Document Frequency", "number"),
        new("total_frequency", "Total Frequency", "number"),
        new("mean_term_frequency", "Mean TF", "number"),
        new("primary_term_count", "Primary Term Count", "number"),
    ];

    private static IReadOnlyList<ExportColumn> RedirectColumns() =>
    [
        new("source_url", "Source URL", "text"),
        new("expected_target_url", "Expected Target", "text"),
        new("ordinal", "Ordinal", "number"),
    ];

    private static IReadOnlyList<ExportColumn> WafColumns() =>
    [
        new("url", "URL", "text"),
        new("provider", "Provider", "text"),
        new("challenge_class", "Challenge Class", "text"),
        new("signature_key", "Signature", "text"),
        new("resolved_by_rendering", "Resolved By Rendering", "boolean"),
        new("detected_at", "Detected At", "datetime"),
        new("evidence_json", "Evidence", "text"),
    ];

    private static IReadOnlyList<ExportColumn> SemanticColumns() =>
    [
        new("source_url", "Source URL", "text"),
        new("target_url", "Target URL", "text"),
        new("similarity", "Similarity", "number"),
        new("anchor_candidates_json", "Anchor Candidates", "text"),
    ];

    private static IReadOnlyList<ExportColumn> AnalyticsColumns() =>
    [
        new("url", "URL", "text"),
        new("vendor", "Vendor", "text"),
        new("container_id", "Container", "text"),
        new("detection_source", "Detection Source", "text"),
        new("placement", "Placement", "text"),
    ];

    // ---- streaming row sources ---------------------------------------------

    private const string NGramSql = "SELECT `zone`, `ngram_order`, `term`, `document_frequency`, `total_frequency`, `mean_term_frequency`, `primary_term_count` FROM `ngram_site_summaries` WHERE `audit_id` = @auditId ORDER BY `zone`, `ngram_order`, `document_frequency` DESC, `term`";
    private const string RedirectSql = "SELECT `source_url`, `expected_target_url`, `ordinal` FROM `redirect_mappings` WHERE `audit_id` = @auditId ORDER BY `ordinal`, `source_url`";
    private const string WafSql = "SELECT u.`canonical_url`, w.`provider`, w.`challenge_class`, w.`signature_key`, w.`resolved_by_rendering`, w.`detected_at`, w.`evidence_json` FROM `waf_detections` w JOIN `url_records` u ON u.`id` = w.`url_id` WHERE w.`audit_id` = @auditId ORDER BY u.`canonical_url`";
    private const string SemanticSql = "SELECT s.`source_url_id`, s.`target_url_id`, s.`similarity`, s.`anchor_candidates_json` FROM `semantic_link_opportunities` s WHERE s.`audit_id` = @auditId ORDER BY s.`similarity` DESC";
    private const string AnalyticsSql = "SELECT u.`canonical_url`, a.`vendor`, a.`container_id`, a.`detection_source`, a.`placement` FROM `analytics_detections` a JOIN `url_records` u ON u.`id` = a.`url_id` WHERE a.`audit_id` = @auditId ORDER BY u.`canonical_url`, a.`vendor`";

    private static string?[] NGramCell(DbDataReader r) =>
    [
        ((TextZone)r.GetInt32(0)).ToString(), Cell(r, 1), Cell(r, 2), Cell(r, 3), Cell(r, 4), Cell(r, 5), Cell(r, 6),
    ];

    private static string?[] RedirectCell(DbDataReader r) => [Cell(r, 0), Cell(r, 1), Cell(r, 2)];

    private static string?[] WafCell(DbDataReader r) =>
    [
        Cell(r, 0), ((WafProvider)r.GetInt32(1)).ToString(), ((WafChallengeClass)r.GetInt32(2)).ToString(),
        Cell(r, 3), BoolCell(r, 4), Cell(r, 5), Cell(r, 6),
    ];

    private static string?[] SemanticCell(DbDataReader r) => [Cell(r, 0), Cell(r, 1), Cell(r, 2), Cell(r, 3)];

    private static string?[] AnalyticsCell(DbDataReader r) => [Cell(r, 0), Cell(r, 1), Cell(r, 2), Cell(r, 3), Cell(r, 4)];

    private async IAsyncEnumerable<ExportRow> StreamSimpleAsync(
        long auditId, string sql, Func<DbDataReader, string?[]> cell, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ExportRow(cell(reader));
        }
    }

    private async IAsyncEnumerable<ExportRow> StreamSummaryAsync(long auditId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT `status`, `phase`, `started_at`, `finished_at`, `counters_json`, `engine_version` FROM `audits` WHERE `id` = @auditId";
            AddAuditParameter(command, auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new ExportRow(["audit", "status", null, ((AuditStatus)reader.GetInt32(0)).ToString(), null]);
                yield return new ExportRow(["audit", "phase", null, ((AuditPhase)reader.GetInt32(1)).ToString(), null]);
                yield return new ExportRow(["audit", "started_at", null, Cell(reader, 2), null]);
                yield return new ExportRow(["audit", "finished_at", null, Cell(reader, 3), null]);
                yield return new ExportRow(["audit", "engine_version", null, Cell(reader, 5), null]);
                yield return new ExportRow(["audit", "counters", null, null, Cell(reader, 4)]);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*), SUM(CASE WHEN `is_indexable` = 1 THEN 1 ELSE 0 END) FROM `url_records` WHERE `audit_id` = @auditId";
            AddAuditParameter(command, auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new ExportRow(["urls", "total", null, Cell(reader, 0), null]);
                yield return new ExportRow(["urls", "indexable", null, Cell(reader, 1), null]);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT `metric_key`, `dimensions_json`, `metric_value` FROM `metric_summaries` WHERE `audit_id` = @auditId ORDER BY `metric_key`";
            AddAuditParameter(command, auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new ExportRow(["metric", Cell(reader, 0), Cell(reader, 1), Cell(reader, 2), null]);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT `category`, `score`, `error_count`, `warning_count`, `notice_count`, `pass_count` FROM `audit_scores` WHERE `audit_id` = @auditId ORDER BY `category`";
            AddAuditParameter(command, auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var category = reader.IsDBNull(0) ? "overall" : ((AuditCategory)reader.GetInt32(0)).ToString();
                var detail = $"errors={Cell(reader, 2)};warnings={Cell(reader, 3)};notices={Cell(reader, 4)};passes={Cell(reader, 5)}";
                yield return new ExportRow(["score", category, null, Cell(reader, 1), detail]);
            }
        }
    }

    private async IAsyncEnumerable<ExportRow> StreamUrlsAsync(long auditId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `canonical_url`, `host`, `path`, `depth`, `discovery_source`, `status_class`, `final_status_code`, `redirect_target_url`, `content_type`, `size_bytes`, `robots_state`, `indexability_flags`, `is_indexable`, `timing_json` FROM `url_records` WHERE `audit_id` = @auditId ORDER BY `canonical_url`";
        AddAuditParameter(command, auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ExportRow(
            [
                Cell(reader, 0), Cell(reader, 1), Cell(reader, 2), Cell(reader, 3),
                ((DiscoverySource)reader.GetInt32(4)).ToString(),
                ((CrawlStatusClass)reader.GetInt32(5)).ToString(),
                Cell(reader, 6), Cell(reader, 7), Cell(reader, 8), Cell(reader, 9),
                ((RobotsState)reader.GetInt32(10)).ToString(),
                Cell(reader, 11), BoolCell(reader, 12), Cell(reader, 13),
            ]);
        }
    }

    private async IAsyncEnumerable<ExportRow> StreamFindingsAsync(long auditId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.`rule_key`, f.`category`, f.`severity`, u.`canonical_url`, f.`evidence_json`, f.`recommendation_key`, f.`is_suppressed` FROM `findings` f LEFT JOIN `url_records` u ON u.`id` = f.`url_id` WHERE f.`audit_id` = @auditId ORDER BY f.`severity`, f.`rule_key`";
        AddAuditParameter(command, auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ExportRow(
            [
                Cell(reader, 0),
                ((AuditCategory)reader.GetInt32(1)).ToString(),
                ((FindingSeverity)reader.GetInt32(2)).ToString(),
                Cell(reader, 3), Cell(reader, 4), Cell(reader, 5), BoolCell(reader, 6),
            ]);
        }
    }

    private async IAsyncEnumerable<ExportRow> StreamLinksAsync(long auditId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.`canonical_url`, l.`canonical_target_url`, t.`canonical_url`, l.`anchor_text`, l.`relation_flags`, l.`is_internal` FROM `link_edges` l JOIN `url_records` s ON s.`id` = l.`source_url_id` LEFT JOIN `url_records` t ON t.`id` = l.`target_url_id` WHERE l.`audit_id` = @auditId ORDER BY s.`canonical_url`, l.`canonical_target_url`";
        AddAuditParameter(command, auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ExportRow([Cell(reader, 0), Cell(reader, 1), Cell(reader, 2), Cell(reader, 3), Cell(reader, 4), BoolCell(reader, 5)]);
        }
    }

    // ---- dynamic datasets (locked DynamicColumnSchema contract) -------------

    private async Task<ExportDataset> BuildExtractionDatasetAsync(long auditId, CancellationToken cancellationToken)
    {
        var rules = await LoadExtractionRulesAsync(auditId, cancellationToken).ConfigureAwait(false);
        var maxOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT `rule_name`, MAX(`ordinal`) FROM `extracted_content` WHERE `audit_id` = @auditId GROUP BY `rule_name`";
            AddAuditParameter(command, auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                maxOrdinals[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        // Rules observed in data but absent from the snapshot keep their columns
        // (first-seen order); configured rules keep configured order.
        foreach (var observed in maxOrdinals.Keys)
        {
            if (!rules.Any(r => string.Equals(r.Name, observed, StringComparison.Ordinal)))
            {
                rules.Add(new CustomExtractionRule(observed, string.Empty, ExtractedValueType.Text, AllowMultiple: true));
            }
        }

        // One representative draft per rule reproduces the busiest-page expansion
        // without materializing values (bounded schema pass).
        var representatives = rules
            .Select(r => new ExtractedValueDraft(
                r.Name,
                maxOrdinals.TryGetValue(r.Name, out var max) ? max : 0,
                null, r.ValueType, IsInvalid: false, IsTruncated: false))
            .ToList();
        var descriptors = DynamicColumnSchema.BuildExtractionColumns(rules, representatives);

        var columns = new List<ExportColumn> { new("url", "URL", "text") };
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            indexByKey[descriptor.Key] = columns.Count;
            var label = descriptor.Key.Contains('.' + descriptor.Ordinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) && descriptor.Key != DynamicColumnSchema.ExtractionColumnPrefix + descriptor.Source
                ? $"{descriptor.Label} [{descriptor.Ordinal}]"
                : descriptor.Label;
            columns.Add(new ExportColumn(descriptor.Key, label, descriptor.ValueType));
        }

        return new ExportDataset(
            ExportDatasetKeys.CustomExtraction,
            columns,
            StreamExtractionRowsAsync(auditId, descriptors, indexByKey, columns.Count, cancellationToken));
    }

    private async IAsyncEnumerable<ExportRow> StreamExtractionRowsAsync(
        long auditId,
        IReadOnlyList<DynamicColumnDescriptor> descriptors,
        Dictionary<string, int> indexByKey,
        int columnCount,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.`url_id`, u.`canonical_url`, e.`rule_name`, e.`ordinal`, e.`typed_value_json`, e.`is_invalid` FROM `extracted_content` e JOIN `url_records` u ON u.`id` = e.`url_id` WHERE e.`audit_id` = @auditId ORDER BY e.`url_id`, e.`rule_name`, e.`ordinal`";
        AddAuditParameter(command, auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        long currentUrl = -1;
        string? currentUrlText = null;
        string?[] cells = new string?[columnCount];
        var hasRow = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var urlId = reader.GetInt64(0);
            if (hasRow && urlId != currentUrl)
            {
                cells[0] = currentUrlText;
                yield return new ExportRow(cells);
                cells = new string?[columnCount];
            }

            currentUrl = urlId;
            currentUrlText = reader.GetString(1);
            hasRow = true;
            var ruleName = reader.GetString(2);
            var ordinal = reader.GetInt32(3);
            var key = descriptors.Any(d => d.Source == ruleName && d.Ordinal == ordinal && d.Key == DynamicColumnSchema.ExtractionColumnPrefix + ruleName)
                ? DynamicColumnSchema.ExtractionColumnPrefix + ruleName
                : DynamicColumnSchema.ExtractionColumnPrefix + ruleName + "." + ordinal.ToString(CultureInfo.InvariantCulture);
            if (indexByKey.TryGetValue(key, out var index))
            {
                // Invalid values emit an empty cell (DynamicColumnSchema §1).
                cells[index] = reader.GetBoolean(5) ? null : RenderTypedJson(reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }

        if (hasRow)
        {
            cells[0] = currentUrlText;
            yield return new ExportRow(cells);
        }
    }

    private async Task<ExportDataset> BuildSnippetDatasetAsync(long auditId, CancellationToken cancellationToken)
    {
        var firstSeen = new List<SnippetResultDraft>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT `snippet_key`, `columns_json` FROM `snippet_results` WHERE `audit_id` = @auditId ORDER BY `id`";
            AddAuditParameter(command, auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                if (seenKeys.Add(key))
                {
                    firstSeen.Add(new SnippetResultDraft(key, reader.GetString(1), "[]", IsTruncated: false));
                }
            }
        }

        var descriptors = DynamicColumnSchema.BuildSnippetColumns(firstSeen);
        var columns = new List<ExportColumn>
        {
            new("url", "URL", "text"),
            new("snippet_key", "Snippet", "text"),
        };
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            indexByKey[descriptor.Key] = columns.Count;
            columns.Add(new ExportColumn(descriptor.Key, descriptor.Label, descriptor.ValueType));
        }

        return new ExportDataset(
            ExportDatasetKeys.Snippets,
            columns,
            StreamSnippetRowsAsync(auditId, indexByKey, columns.Count, cancellationToken));
    }

    private async IAsyncEnumerable<ExportRow> StreamSnippetRowsAsync(
        long auditId,
        Dictionary<string, int> indexByKey,
        int columnCount,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT u.`canonical_url`, s.`snippet_key`, s.`rows_json` FROM `snippet_results` s JOIN `url_records` u ON u.`id` = s.`url_id` WHERE s.`audit_id` = @auditId ORDER BY s.`id`";
        AddAuditParameter(command, auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var url = reader.GetString(0);
            var snippetKey = reader.GetString(1);
            using var rows = JsonDocument.Parse(reader.GetString(2));
            if (rows.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in rows.RootElement.EnumerateArray())
            {
                var cells = new string?[columnCount];
                cells[0] = url;
                cells[1] = snippetKey;
                if (row.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in row.EnumerateObject())
                    {
                        var key = DynamicColumnSchema.SnippetColumnPrefix + snippetKey + "." + property.Name;
                        if (indexByKey.TryGetValue(key, out var index))
                        {
                            cells[index] = RenderJsonElement(property.Value);
                        }
                    }
                }

                yield return new ExportRow(cells);
            }
        }
    }

    private async Task<List<CustomExtractionRule>> LoadExtractionRulesAsync(long auditId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.`custom_extraction_rules_json` FROM `audits` a JOIN `crawl_configurations` c ON c.`id` = a.`configuration_id` WHERE a.`id` = @auditId";
        AddAuditParameter(command, auditId);
        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CustomExtractionRule>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ---- rendering helpers ---------------------------------------------------

    private static void AddAuditParameter(DbCommand command, long auditId)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
    }

    private static string BoolCell(DbDataReader reader, int ordinal)
        => reader.GetBoolean(ordinal) ? "true" : "false";

    private static string? Cell(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("O", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static string? RenderTypedJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return RenderJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RenderJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };
}
