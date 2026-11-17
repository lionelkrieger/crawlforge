using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.LogAnalyzer;

/// <summary>
/// Offline streaming log import service (FS-CRAWL-002 §11.2/§11.4/§11.8).
/// Streams a local file through the parser, classifies each hit offline
/// (bundled UA + CIDR only), canonicalizes under the audit query policy, and
/// aggregates per (import, canonical URL) with bounded memory: compact
/// per-URL state, capped query-variant sets, capped bot-IP sample. Aggregates
/// persist in transactional batches; progress emits at most once per second
/// through the locked hub. An import never blocks or fails a crawl.
/// </summary>
public sealed class LogImportService : ILogImportService
{
    /// <summary>Rows per transactional aggregate batch.</summary>
    public const int AggregateBatchSize = 500;

    /// <summary>Maximum distinct query variants tracked per URL (bounded memory).</summary>
    public const int MaxVariantsPerUrl = 256;

    /// <summary>Maximum bot-IP samples retained for the consented DNS tool.</summary>
    public const int MaxBotIpSamples = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbProvider _provider;
    private readonly IStreamingLogFileParser _parser;
    private readonly IBotVerifier _verifier;
    private readonly IUrlCanonicalizer _canonicalizer;
    private readonly IRealtimeHub _hub;
    private readonly BotCatalogue _catalogue;
    private readonly IReadOnlyDictionary<string, bool> _searchByFamily;

    /// <summary>Creates the import service.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="parser">The streaming parser (per-import instance).</param>
    /// <param name="verifier">The offline bot verifier.</param>
    /// <param name="canonicalizer">The URL canonicalizer (audit query policy).</param>
    /// <param name="hub">The realtime hub for progress/completion events.</param>
    /// <param name="catalogue">The bundled bot catalogue (version metadata + purpose map).</param>
    public LogImportService(
        IDbProvider provider,
        IStreamingLogFileParser parser,
        IBotVerifier verifier,
        IUrlCanonicalizer canonicalizer,
        IRealtimeHub hub,
        BotCatalogue catalogue)
    {
        _provider = provider;
        _parser = parser;
        _verifier = verifier;
        _canonicalizer = canonicalizer;
        _hub = hub;
        _catalogue = catalogue;
        _searchByFamily = catalogue.Families.ToDictionary(f => f.Name, f => f.IsSearchBot);
    }

    /// <inheritdoc />
    public async Task<LogImportOutcome> ImportAsync(LogImportRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceFilePath))
        {
            throw new FileNotFoundException("The log file does not exist on local disk.", request.SourceFilePath);
        }

        if (!Uri.TryCreate(request.BaseUri, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("The base URI must be absolute.", nameof(request));
        }

        var importedAt = DateTimeOffset.UtcNow;
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var fileStream = new FileStream(
            request.SourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.SequentialScan);

        // Detect before persisting so the import row records the actual format.
        var format = request.Format ?? await LogFormatDetector.DetectAsync(fileStream, cancellationToken);
        var importId = await InsertImportRowAsync(connection, request with { Format = format }, importedAt, cancellationToken);

        var aggregates = new Dictionary<string, UrlAggregation>(StringComparer.Ordinal);
        var botIpSamples = new List<BotIpSample>();
        var sampledIps = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? firstSeen = null;
        DateTimeOffset? lastSeen = null;
        long bytesServed = 0;
        long queryVariantTotal = 0;
        var progressClock = Stopwatch.StartNew();

        try
        {
            _parser.Configure(new LogParserOptions(
                WindowStart: request.WindowStart,
                WindowEnd: request.WindowEnd));

            await foreach (var entry in _parser.ParseAsync(fileStream, format, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var canonical = TryCanonicalize(entry.Path, baseUri);
                if (canonical is null || !Uri.TryCreate(canonical, UriKind.Absolute, out var canonicalUri))
                {
                    _parser.Counters.LinesAccepted--;
                    RejectSample($"unresolvable path: {entry.Path}");
                    continue;
                }

                var classification = _verifier.Classify(entry.ClientIp, entry.UserAgent);
                var family = classification.Family ?? BotVerifier.UnknownLabel;
                var isSearch = classification.IsBot && _searchByFamily.GetValueOrDefault(family);

                if (!aggregates.TryGetValue(canonical, out var aggregation))
                {
                    aggregation = new UrlAggregation(canonical, canonicalUri.Host);
                    aggregates[canonical] = aggregation;
                }

                aggregation.TotalHits++;
                if (isSearch)
                {
                    aggregation.SearchBotHits++;
                }

                aggregation.Families[family] = aggregation.Families.GetValueOrDefault(family) + 1;
                var statusClass = $"{entry.StatusCode / 100}xx";
                aggregation.StatusClasses[statusClass] = aggregation.StatusClasses.GetValueOrDefault(statusClass) + 1;
                aggregation.FirstSeen = aggregation.FirstSeen is { } fs && fs < entry.Timestamp ? fs : entry.Timestamp;
                aggregation.LastSeen = aggregation.LastSeen is { } ls && ls > entry.Timestamp ? ls : entry.Timestamp;
                aggregation.Bytes += entry.ResponseBytes ?? 0;
                queryVariantTotal += TrackQueryVariant(aggregation, entry.Path);

                firstSeen = firstSeen is { } wfs && wfs < entry.Timestamp ? wfs : entry.Timestamp;
                lastSeen = lastSeen is { } wls && wls > entry.Timestamp ? wls : entry.Timestamp;
                bytesServed += entry.ResponseBytes ?? 0;

                if (classification.IsBot &&
                    entry.ClientIp is not null &&
                    botIpSamples.Count < MaxBotIpSamples &&
                    sampledIps.Add(entry.ClientIp))
                {
                    botIpSamples.Add(new BotIpSample(
                        entry.ClientIp,
                        family,
                        entry.UserAgent ?? string.Empty,
                        BotVerificationTiers.FromClassification(classification)));
                }

                if (progressClock.ElapsedMilliseconds >= 1000)
                {
                    progressClock.Restart();
                    await PublishProgressAsync(importId, "parsing", cancellationToken);
                }
            }

            await PublishProgressAsync(importId, "persisting", cancellationToken);
            await PersistAggregatesAsync(importId, aggregates, cancellationToken);

            var counters = BuildCountersPayload(
                aggregates.Count, firstSeen, lastSeen, bytesServed, queryVariantTotal, botIpSamples);
            var windowStart = request.WindowStart ?? firstSeen ?? DateTimeOffset.UnixEpoch;
            var windowEnd = request.WindowEnd ?? lastSeen ?? DateTimeOffset.UnixEpoch;
            await FinalizeImportAsync(connection, importId, LogImportStatus.Completed, counters, windowStart, windowEnd, cancellationToken);
            await PublishCompletedAsync(importId, request.ProjectId, LogImportStatus.Completed, counters, aggregates, cancellationToken);
            return new LogImportOutcome(importId, LogImportStatus.Completed, counters, null);
        }
        catch (Exception exception)
        {
            var counters = BuildCountersPayload(
                aggregates.Count, firstSeen, lastSeen, bytesServed, queryVariantTotal, botIpSamples);
            await FinalizeImportAsync(
                connection, importId, LogImportStatus.Failed, counters,
                request.WindowStart ?? DateTimeOffset.UnixEpoch,
                request.WindowEnd ?? DateTimeOffset.UnixEpoch,
                CancellationToken.None);
            await PublishCompletedAsync(importId, request.ProjectId, LogImportStatus.Failed, counters, aggregates, CancellationToken.None);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            return new LogImportOutcome(importId, LogImportStatus.Failed, counters, exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogImportRecord>> ListImportsAsync(long projectId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        var records = new List<LogImportRecord>();
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, project_id, format, window_start, window_end, counters_json, verification_mode, status, imported_at, source_file_path " +
            "FROM log_imports WHERE project_id = @project ORDER BY imported_at DESC, id DESC LIMIT @limit OFFSET @offset";
        AddParameter(command, "@project", projectId);
        AddParameter(command, "@limit", limit);
        AddParameter(command, "@offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var import = new LogImportDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                (LogFileFormat)reader.GetInt32(2),
                ParseTimestamp(reader.GetString(3)),
                ParseTimestamp(reader.GetString(4)),
                reader.GetString(5),
                (BotVerificationMode)reader.GetInt32(6),
                (LogImportStatus)reader.GetInt32(7),
                ParseTimestamp(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9));
            var counters = JsonSerializer.Deserialize<LogImportCountersPayload>(import.CountersJson, JsonOptions)
                ?? EmptyCounters();
            records.Add(new LogImportRecord(import, counters));
        }

        return records;
    }

    /// <inheritdoc />
    public async Task PurgeImportAsync(long importId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM log_url_aggregates WHERE log_import_id = @id";
            AddParameter(command, "@id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM log_imports WHERE id = @id";
            AddParameter(command, "@id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private string? TryCanonicalize(string path, Uri baseUri)
    {
        try
        {
            var absolute = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? path
                : baseUri.ToString().TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path);
            return _canonicalizer.Canonicalize(absolute);
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static long TrackQueryVariant(UrlAggregation aggregation, string path)
    {
        var queryIndex = path.IndexOf('?');
        if (queryIndex < 0 || queryIndex == path.Length - 1)
        {
            return 0;
        }

        var variant = path[(queryIndex + 1)..];
        if (aggregation.QueryVariants.Count < MaxVariantsPerUrl)
        {
            return aggregation.QueryVariants.Add(variant) ? 1 : 0;
        }

        aggregation.VariantOverflow++;
        return 1;
    }

    private void RejectSample(string sample)
    {
        _parser.Counters.LinesRejected++;
        if (_parser.Counters.ErrorSamples.Count < 100)
        {
            _parser.Counters.ErrorSamples.Add(sample.Length <= 200 ? sample : sample[..200]);
        }
    }

    private LogImportCountersPayload BuildCountersPayload(
        long uniqueUrls,
        DateTimeOffset? firstSeen,
        DateTimeOffset? lastSeen,
        long bytesServed,
        long queryVariantTotal,
        IReadOnlyList<BotIpSample> botIpSamples)
    {
        var counters = _parser.Counters;
        return new LogImportCountersPayload(
            counters.LinesParsed,
            counters.LinesAccepted,
            counters.LinesRejected,
            counters.LinesOutOfWindow,
            counters.ErrorSamples.ToList(),
            firstSeen,
            lastSeen,
            bytesServed,
            queryVariantTotal,
            uniqueUrls,
            botIpSamples,
            _catalogue.Version,
            _catalogue.Revision);
    }

    private static LogImportCountersPayload EmptyCounters() =>
        new(0, 0, 0, 0, [], null, null, 0, 0, 0, [], null, null);

    private async Task<long> InsertImportRowAsync(DbConnection connection, LogImportRequest request, DateTimeOffset importedAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO log_imports (project_id, format, window_start, window_end, counters_json, verification_mode, status, imported_at, source_file_path) " +
            "VALUES (@project, @format, @windowStart, @windowEnd, '{}', @verification, @status, @importedAt, @path)";
        AddParameter(command, "@project", request.ProjectId);
        AddParameter(command, "@format", (int)(request.Format ?? LogFileFormat.ApacheNginxCombined));
        AddParameter(command, "@windowStart", (request.WindowStart ?? DateTimeOffset.UnixEpoch).ToString("O"));
        AddParameter(command, "@windowEnd", (request.WindowEnd ?? DateTimeOffset.UnixEpoch).ToString("O"));
        AddParameter(command, "@verification", (int)request.VerificationMode);
        AddParameter(command, "@status", (int)LogImportStatus.Importing);
        AddParameter(command, "@importedAt", importedAt.ToString("O"));
        AddParameter(command, "@path", request.SourceFilePath);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var idCommand = connection.CreateCommand();
        idCommand.CommandText = _provider.Kind == DatabaseProviderKind.Sqlite
            ? "SELECT last_insert_rowid()"
            : "SELECT LAST_INSERT_ID()";
        var result = await idCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task PersistAggregatesAsync(long importId, Dictionary<string, UrlAggregation> aggregates, CancellationToken cancellationToken)
    {
        var batch = new List<UrlAggregation>(AggregateBatchSize);
        foreach (var aggregation in aggregates.Values)
        {
            batch.Add(aggregation);
            if (batch.Count == AggregateBatchSize)
            {
                await InsertAggregateBatchAsync(importId, batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await InsertAggregateBatchAsync(importId, batch, cancellationToken);
        }
    }

    private async Task InsertAggregateBatchAsync(long importId, List<UrlAggregation> batch, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var aggregation in batch)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO log_url_aggregates (log_import_id, canonical_url, host, total_hits, search_bot_hits, bot_family_breakdown_json, status_breakdown_json) " +
                "VALUES (@import, @url, @host, @total, @search, @families, @statuses)";
            AddParameter(command, "@import", importId);
            AddParameter(command, "@url", aggregation.CanonicalUrl);
            AddParameter(command, "@host", aggregation.Host);
            AddParameter(command, "@total", aggregation.TotalHits);
            AddParameter(command, "@search", aggregation.SearchBotHits);
            AddParameter(command, "@families", JsonSerializer.Serialize(aggregation.Families, JsonOptions));
            AddParameter(command, "@statuses", JsonSerializer.Serialize(aggregation.StatusClasses, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task FinalizeImportAsync(
        DbConnection connection,
        long importId,
        LogImportStatus status,
        LogImportCountersPayload counters,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE log_imports SET status = @status, counters_json = @counters, window_start = @windowStart, window_end = @windowEnd WHERE id = @id";
        AddParameter(command, "@status", (int)status);
        AddParameter(command, "@counters", JsonSerializer.Serialize(counters, JsonOptions));
        AddParameter(command, "@windowStart", windowStart.ToString("O"));
        AddParameter(command, "@windowEnd", windowEnd.ToString("O"));
        AddParameter(command, "@id", importId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Emits log-import-progress with the locked payload shape
    /// (contracts/hub/events.ts LogImportProgressPayload):
    /// { importId, counters: { linesParsed, linesAccepted, linesRejected }, phase }.
    /// </summary>
    private async Task PublishProgressAsync(long importId, string phase, CancellationToken cancellationToken)
    {
        var counters = _parser.Counters;
        var payload = JsonSerializer.Serialize(new
        {
            importId,
            counters = new
            {
                linesParsed = counters.LinesParsed,
                linesAccepted = counters.LinesAccepted,
                linesRejected = counters.LinesRejected
            },
            phase
        }, JsonOptions);
        await _hub.PublishAsync(
            new HubEvent(
                LogHubEventNames.LogImportProgress,
                payload,
                ThrottleKey: $"log-import-{importId}",
                ThrottleInterval: TimeSpan.FromSeconds(1)),
            cancellationToken);
    }

    /// <summary>
    /// Emits log-import-completed with the locked payload shape
    /// (contracts/hub/events.ts LogImportCompletedPayload):
    /// { importId, finalCounters: { linesParsed, linesAccepted, linesRejected },
    ///   correlation: { orphanCount, budgetWasteCount, uncrawledIndexableCount } }.
    /// Correlation is computed at finalize time against the project's latest
    /// COMPLETED audit via the accepted LogCorrelationEngine. When the project
    /// has no completed audit yet — or the import failed — the three correlation
    /// counts are emitted as zeros (no crawl baseline exists to correlate
    /// against; correlation remains recomputable on demand once an audit
    /// completes). The status/counters/summary fields are additive extras.
    /// </summary>
    private async Task PublishCompletedAsync(
        long importId,
        long projectId,
        LogImportStatus status,
        LogImportCountersPayload counters,
        Dictionary<string, UrlAggregation> aggregates,
        CancellationToken cancellationToken)
    {
        long totalHits = 0;
        long searchBotHits = 0;
        long searchBotUrls = 0;
        foreach (var aggregation in aggregates.Values)
        {
            totalHits += aggregation.TotalHits;
            searchBotHits += aggregation.SearchBotHits;
            if (aggregation.SearchBotHits > 0)
            {
                searchBotUrls++;
            }
        }

        var correlation = await ComputeFinalizeCorrelationAsync(projectId, importId, status, cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            importId,
            finalCounters = new
            {
                linesParsed = counters.LinesParsed,
                linesAccepted = counters.LinesAccepted,
                linesRejected = counters.LinesRejected
            },
            correlation,
            status = status.ToString().ToLowerInvariant(),
            counters,
            summary = new
            {
                uniqueUrls = aggregates.Count,
                searchBotUrls,
                totalHits,
                searchBotHits
            }
        }, JsonOptions);
        await _hub.PublishAsync(new HubEvent(LogHubEventNames.LogImportCompleted, payload), cancellationToken);
    }

    /// <summary>
    /// Locked LogCorrelationSummary counts at finalize time: latest completed
    /// audit of the project, or zeros when none exists / the import failed.
    /// </summary>
    private async Task<object> ComputeFinalizeCorrelationAsync(long projectId, long importId, LogImportStatus status, CancellationToken cancellationToken)
    {
        if (status != LogImportStatus.Completed)
        {
            return new { orphanCount = 0L, budgetWasteCount = 0L, uncrawledIndexableCount = 0L };
        }

        long? auditId = null;
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id FROM audits WHERE project_id = @project AND status = @completed AND is_deleted = 0 ORDER BY id DESC LIMIT 1";
            AddParameter(command, "@project", projectId);
            AddParameter(command, "@completed", (int)AuditStatus.Completed);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null and not DBNull)
            {
                auditId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }
        }

        if (auditId is null)
        {
            return new { orphanCount = 0L, budgetWasteCount = 0L, uncrawledIndexableCount = 0L };
        }

        var engine = new LogCorrelationEngine(_provider);
        var result2 = await engine.CorrelateAsync(auditId.Value, importId, cancellationToken);
        return new
        {
            orphanCount = (long)result2.TrueOrphans.Count,
            budgetWasteCount = (long)result2.BudgetWaste.Count,
            uncrawledIndexableCount = (long)result2.UncrawledIndexable.Count
        };
    }

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Compact per-URL aggregation state (bounded: capped variant set).</summary>
    private sealed class UrlAggregation
    {
        public UrlAggregation(string canonicalUrl, string host)
        {
            CanonicalUrl = canonicalUrl;
            Host = host;
        }

        public string CanonicalUrl { get; }
        public string Host { get; }
        public long TotalHits { get; set; }
        public long SearchBotHits { get; set; }
        public Dictionary<string, long> Families { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> StatusClasses { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? FirstSeen { get; set; }
        public DateTimeOffset? LastSeen { get; set; }
        public long Bytes { get; set; }
        public HashSet<string> QueryVariants { get; } = new(StringComparer.Ordinal);
        public long VariantOverflow { get; set; }
    }
}
