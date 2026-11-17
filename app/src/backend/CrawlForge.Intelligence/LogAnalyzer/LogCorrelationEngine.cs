using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.LogAnalyzer;

/// <summary>
/// Local correlation options.
/// </summary>
/// <param name="BudgetWasteThreshold">Minimum search-bot hits for the waste view (default 10, §11.5).</param>
/// <param name="ProjectHosts">In-scope hosts for the orphan view; null derives from the audit's crawled hosts.</param>
public sealed record LogCorrelationOptions(
    long BudgetWasteThreshold = 10,
    IReadOnlyList<string>? ProjectHosts = null);

/// <summary>
/// Detailed correlation outcome: the three views with per-class evidence,
/// out-of-scope orphans reported separately (§11.5), finding drafts in the
/// Log Intelligence category, and metric contributions.
/// </summary>
/// <param name="TrueOrphans">Search-bot-hit URLs never discovered by the crawl.</param>
/// <param name="BudgetWaste">Low-value URLs consuming search-bot budget.</param>
/// <param name="UncrawledIndexable">Indexable crawled URLs with zero search-bot hits.</param>
/// <param name="OutOfScopeOrphans">True orphans on out-of-scope hosts.</param>
/// <param name="Findings">Finding drafts (logs.* rule keys).</param>
/// <param name="Metrics">Metric contributions (log-* keys).</param>
public sealed record LogCorrelationDetailedResult(
    IReadOnlyList<LogCorrelationRow> TrueOrphans,
    IReadOnlyList<LogCorrelationRow> BudgetWaste,
    IReadOnlyList<LogCorrelationRow> UncrawledIndexable,
    IReadOnlyList<LogCorrelationRow> OutOfScopeOrphans,
    IReadOnlyList<FindingDraft> Findings,
    IReadOnlyList<MetricContribution> Metrics);

/// <summary>
/// Crawl-vs-log correlation engine (FS-CRAWL-002 §11.5). Joins log_url_aggregates
/// against the audit's url_records by CANONICAL URL and the internal link graph
/// for inlink evidence. Three recomputable views: True Orphans (search-bot hits,
/// absent from the crawl), Crawl Budget Waste (search-bot frequency ≥ threshold
/// on non-2xx / non-indexable / query-trap URLs), Uncrawled Indexable (indexable
/// crawled URLs with zero search-bot hits). All passes stream with compact
/// identifier-keyed state (bounded memory at 500k+ URLs).
/// </summary>
public sealed class LogCorrelationEngine : ILogCorrelationEngine, ILogCorrelationReadModel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbProvider _provider;
    private readonly LogCorrelationOptions _options;

    /// <summary>Creates the correlation engine.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="options">Local options; null uses the documented defaults.</param>
    public LogCorrelationEngine(IDbProvider provider, LogCorrelationOptions? options = null)
    {
        _provider = provider;
        _options = options ?? new LogCorrelationOptions();
    }

    /// <inheritdoc />
    public async Task<LogCorrelationResult> CorrelateAsync(long auditId, long logImportId, CancellationToken cancellationToken = default)
    {
        var detailed = await CorrelateDetailedAsync(auditId, logImportId, cancellationToken);
        return new LogCorrelationResult(
            detailed.TrueOrphans.Select(ToContractRow).ToList(),
            detailed.BudgetWaste.Select(ToContractRow).ToList(),
            detailed.UncrawledIndexable.Select(ToContractRow).ToList());
    }

    /// <summary>
    /// Computes the detailed correlation with evidence, findings, and metrics.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="logImportId">Import identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detailed outcome.</returns>
    public async Task<LogCorrelationDetailedResult> CorrelateDetailedAsync(long auditId, long logImportId, CancellationToken cancellationToken = default)
    {
        var (windowStart, windowEnd) = await LoadImportWindowAsync(logImportId, cancellationToken);

        // Pass 1: stream the audit's URLs into compact canonical-keyed state.
        var crawled = new Dictionary<string, CrawledUrl>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, canonical_url, host, depth, status_class, is_indexable FROM url_records WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var canonical = reader.GetString(1);
                var host = reader.GetString(2);
                hosts.Add(host);
                crawled[canonical] = new CrawledUrl(
                    reader.GetInt64(0),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt64(5) != 0);
            }
        }

        // Pass 2: internal inlink counts for uncrawled-indexable evidence.
        var inlinks = new Dictionary<long, long>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT source_url_id, target_url_id FROM link_edges " +
                "WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NOT NULL";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var source = reader.GetInt64(0);
                var target = reader.GetInt64(1);
                if (target != source)
                {
                    inlinks[target] = inlinks.GetValueOrDefault(target) + 1;
                }
            }
        }

        var scopeHosts = _options.ProjectHosts is { Count: > 0 }
            ? new HashSet<string>(_options.ProjectHosts, StringComparer.OrdinalIgnoreCase)
            : hosts;

        // Pass 3: stream the import's aggregates into the three views.
        var trueOrphans = new List<LogCorrelationRow>();
        var outOfScope = new List<LogCorrelationRow>();
        var budgetWaste = new List<LogCorrelationRow>();
        var searchHitUrls = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<FindingDraft>();
        long botHitsTotal = 0;

        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT canonical_url, host, total_hits, search_bot_hits, bot_family_breakdown_json, status_breakdown_json " +
                "FROM log_url_aggregates WHERE log_import_id = @import ORDER BY id";
            AddParameter(command, "@import", logImportId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var canonical = reader.GetString(0);
                var host = reader.GetString(1);
                var totalHits = reader.GetInt64(2);
                var searchHits = reader.GetInt64(3);
                var families = ParseBreakdown(reader.IsDBNull(4) ? null : reader.GetString(4));
                var statuses = ParseBreakdown(reader.IsDBNull(5) ? null : reader.GetString(5));
                botHitsTotal += totalHits;
                if (searchHits > 0)
                {
                    searchHitUrls.Add(canonical);
                }

                var dominantFamily = DominantFamily(families);
                var inCrawl = crawled.TryGetValue(canonical, out var crawledUrl);

                // True orphans: search-bot hits, never discovered by the crawl.
                if (searchHits > 0 && !inCrawl)
                {
                    var evidence = JsonSerializer.Serialize(new
                    {
                        hitCount = totalHits,
                        searchBotHits = searchHits,
                        families,
                        windowStart,
                        windowEnd
                    }, JsonOptions);
                    var row = new LogCorrelationRow(canonical, totalHits, searchHits, dominantFamily, false, evidence);
                    if (scopeHosts.Contains(host))
                    {
                        trueOrphans.Add(row);
                        findings.Add(new FindingDraft(LogRuleKeys.TrueOrphan, AuditCategory.LogIntelligence, FindingSeverity.Warning, null, evidence, null));
                    }
                    else
                    {
                        outOfScope.Add(row);
                    }
                }

                // Budget waste: threshold + (non-2xx mix | non-indexable | query trap).
                if (searchHits >= _options.BudgetWasteThreshold)
                {
                    var non2xx = statuses.Where(kv => kv.Key != "2xx" && kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
                    var nonIndexable = inCrawl && !crawledUrl!.IsIndexable;
                    // Documented fallback for the deferred query-variant column:
                    // query presence in the canonical URL marks trap candidates.
                    var queryPresent = canonical.Contains('?');
                    if (non2xx.Count > 0 || nonIndexable || queryPresent)
                    {
                        var evidence = JsonSerializer.Serialize(new
                        {
                            searchBotHits = searchHits,
                            threshold = _options.BudgetWasteThreshold,
                            statusMix = statuses,
                            indexable = inCrawl ? crawledUrl!.IsIndexable : (bool?)null,
                            queryPresent,
                            families
                        }, JsonOptions);
                        budgetWaste.Add(new LogCorrelationRow(canonical, totalHits, searchHits, dominantFamily, inCrawl, evidence));
                        findings.Add(new FindingDraft(
                            LogRuleKeys.CrawlBudgetWaste, AuditCategory.LogIntelligence, FindingSeverity.Warning,
                            inCrawl ? crawledUrl!.Id : null, evidence, null));
                    }
                }
            }
        }

        // Uncrawled indexable: indexable crawled URLs with zero search-bot hits.
        var uncrawled = new List<LogCorrelationRow>();
        foreach (var (canonical, url) in crawled)
        {
            if (!url.IsIndexable || searchHitUrls.Contains(canonical))
            {
                continue;
            }

            var evidence = JsonSerializer.Serialize(new
            {
                crawlDepth = url.Depth,
                inlinkCount = inlinks.GetValueOrDefault(url.Id),
                windowStart,
                windowEnd,
                caveat = "zero search-bot hits within the selected window; extend the window before acting"
            }, JsonOptions);
            uncrawled.Add(new LogCorrelationRow(canonical, 0, 0, null, true, evidence));
            findings.Add(new FindingDraft(LogRuleKeys.UncrawledIndexable, AuditCategory.LogIntelligence, FindingSeverity.Notice, url.Id, evidence, null));
        }

        trueOrphans.Sort(static (a, b) => b.SearchBotHits.CompareTo(a.SearchBotHits));
        outOfScope.Sort(static (a, b) => b.SearchBotHits.CompareTo(a.SearchBotHits));
        budgetWaste.Sort(static (a, b) => b.SearchBotHits.CompareTo(a.SearchBotHits));
        uncrawled.Sort(static (a, b) => string.CompareOrdinal(a.CanonicalUrl, b.CanonicalUrl));

        var metrics = new List<MetricContribution>
        {
            new(MetricKeys.LogTrueOrphans, JsonSerializer.Serialize(new { scope = "in-scope" }), trueOrphans.Count),
            new(MetricKeys.LogBudgetWaste, "{}", budgetWaste.Count),
            new(MetricKeys.LogUncrawledIndexable, "{}", uncrawled.Count),
            new(MetricKeys.LogBotHits, JsonSerializer.Serialize(new { family = "all", tier = "all" }), botHitsTotal)
        };

        return new LogCorrelationDetailedResult(trueOrphans, budgetWaste, uncrawled, outOfScope, findings, metrics);
    }

    /// <inheritdoc />
    public async Task<LogCorrelationPage> GetCorrelationPageAsync(
        long auditId,
        long logImportId,
        LogCorrelationView view,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var detailed = await CorrelateDetailedAsync(auditId, logImportId, cancellationToken);
        IReadOnlyList<LogCorrelationRow> rows = view switch
        {
            LogCorrelationView.Orphans => detailed.TrueOrphans,
            LogCorrelationView.Waste => detailed.BudgetWaste,
            LogCorrelationView.Uncrawled => detailed.UncrawledIndexable,
            _ => BuildSummaryRows(detailed)
        };

        var page = rows.Skip(offset).Take(limit).ToList();
        return new LogCorrelationPage(view, page, rows.Count, detailed.OutOfScopeOrphans);
    }

    private static IReadOnlyList<LogCorrelationRow> BuildSummaryRows(LogCorrelationDetailedResult detailed) =>
    [
        new(LogRuleKeys.TrueOrphan, detailed.TrueOrphans.Count, detailed.TrueOrphans.Count, null, false,
            JsonSerializer.Serialize(new { view = "orphans", outOfScope = detailed.OutOfScopeOrphans.Count })),
        new(LogRuleKeys.CrawlBudgetWaste, detailed.BudgetWaste.Count, detailed.BudgetWaste.Count, null, false,
            JsonSerializer.Serialize(new { view = "waste" })),
        new(LogRuleKeys.UncrawledIndexable, detailed.UncrawledIndexable.Count, detailed.UncrawledIndexable.Count, null, false,
            JsonSerializer.Serialize(new { view = "uncrawled" }))
    ];

    private async Task<(DateTimeOffset Start, DateTimeOffset End)> LoadImportWindowAsync(long logImportId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT window_start, window_end FROM log_imports WHERE id = @id";
        AddParameter(command, "@id", logImportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Log import {logImportId} does not exist.");
        }

        return (ParseTimestamp(reader.GetString(0)), ParseTimestamp(reader.GetString(1)));
    }

    private static Dictionary<string, long> ParseBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, long>>(json, JsonOptions) ?? new Dictionary<string, long>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    private static string? DominantFamily(Dictionary<string, long> families) =>
        families.Count == 0 ? null : families.OrderByDescending(kv => kv.Value).First().Key;

    private static LogCorrelatedUrl ToContractRow(LogCorrelationRow row) =>
        new(row.CanonicalUrl, row.HitCount, row.BotFamily);

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record CrawledUrl(long Id, int Depth, int StatusClass, bool IsIndexable);
}
