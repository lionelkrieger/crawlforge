using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.Scale.Benchmarks.Support;

/// <summary>Outcome of one deterministic dataset seed.</summary>
/// <param name="ProjectId">Owning project identifier.</param>
/// <param name="ConfigurationId">Crawl configuration identifier.</param>
/// <param name="AuditId">Completed audit the explorers browse.</param>
/// <param name="UrlCount">Persisted URL records.</param>
/// <param name="FindingCount">Persisted findings.</param>
/// <param name="LinkCount">Persisted link edges.</param>
/// <param name="ExtractionCount">Persisted custom-extraction values.</param>
/// <param name="NgramCount">Persisted n-gram frequency rows.</param>
/// <param name="SiteSummaryCount">Persisted n-gram site-summary rows.</param>
/// <param name="MetricCount">Persisted metric summaries.</param>
/// <param name="SeedSeconds">Wall-clock seed duration.</param>
public sealed record DatasetSummary(
    long ProjectId,
    long ConfigurationId,
    long AuditId,
    long UrlCount,
    long FindingCount,
    long LinkCount,
    long ExtractionCount,
    long NgramCount,
    long SiteSummaryCount,
    long MetricCount,
    double SeedSeconds);

/// <summary>
/// Deterministic-seed 500k+ URL dataset loaded through the REAL persistence
/// stack: product repositories (<see cref="IWriteRepository{TDto}"/>) over the
/// product <see cref="IDbProvider"/> connection, inside shared transactions
/// committed every 10,000 rows (bounded memory — rows are generated and
/// consumed one at a time, never materialized as a full dataset).
///
/// The same seed always produces the same rows, so the browser benchmark can
/// reuse a previously seeded database after verifying the manifest.
/// </summary>
public sealed class DeterministicDataset
{
    /// <summary>Fixed generator seed (determinism contract).</summary>
    public const int GeneratorSeed = 20261114;

    /// <summary>URL records to generate (criterion #40 scale target).</summary>
    public const int UrlCount = 500_000;

    /// <summary>Finding rows to generate.</summary>
    public const int FindingCount = 150_000;

    /// <summary>Link edges to generate.</summary>
    public const int LinkCount = 600_000;

    /// <summary>Custom-extraction values to generate (three dynamic rules).</summary>
    public const int ExtractionCount = 180_000;

    /// <summary>N-gram frequency rows to generate.</summary>
    public const int NgramCount = 120_000;

    /// <summary>
    /// N-gram site-summary rows to generate. The N-Gram Explorer's default
    /// view is scope=site, which reads the materialized ngram_site_summaries
    /// table — per-URL frequency rows alone leave that view empty.
    /// </summary>
    public const int SiteSummaryCount = 120_000;

    private const int CommitEvery = 10_000;
    private const int HostPoolSize = 25;
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] RuleKeys =
    [
        "title-length", "meta-description-length", "broken-link", "redirect-chain",
        "missing-h1", "duplicate-title", "canonical-mismatch", "mixed-content",
        "slow-ttfb", "missing-alt", "orphan-page", "hreflang-conflict"
    ];

    private static readonly string[] ExtractionRules = ["product-price", "stock-status", "rating"];

    private static readonly string[] TermPool =
    [
        "crawl", "audit", "seo", "index", "link", "page", "content", "schema",
        "robot", "signal", "canonical", "redirect", "anchor", "title", "meta",
        "heading", "image", "script", "style", "server", "render", "fetch",
        "frontier", "politeness", "sitemap", "hreflang", "structured", "snippet",
        "keyword", "density", "orphan", "equity", "zone", "budget", "consent",
        "container", "beacon", "governance", "embedding", "cluster"
    ];

    private readonly IServiceProvider _services;

    /// <summary>Creates the dataset seeder over a started product backend.</summary>
    /// <param name="services">Product service provider (migrated database).</param>
    public DeterministicDataset(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Seeds the full dataset and returns the summary.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Seed summary with identifiers and counts.</returns>
    public async Task<DatasetSummary> SeedAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(GeneratorSeed);

        var project = await _services.GetRequiredService<IWriteRepository<ProjectDto>>().CreateAsync(
            new ProjectDto(0, "Scale Benchmark 500k", "Deterministic WP-07-03 scale dataset", null,
                BaseTime, BaseTime, null, false), cancellationToken);

        var configuration = await _services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>().CreateAsync(
            new CrawlConfigurationDto(0, project.Id, "scale-config",
                """["https://site-0.example.test/"]""", null, null, null, RenderMode.None,
                null, null,
                """[{"name":"product-price","selector":".price","type":"number"},{"name":"stock-status","selector":".stock","type":"boolean"},{"name":"rating","selector":".rating","type":"number"}]""",
                null, null, BaseTime, BaseTime), cancellationToken);

        var audit = await _services.GetRequiredService<IWriteRepository<AuditDto>>().CreateAsync(
            new AuditDto(0, project.Id, configuration.Id, "{}", AuditStatus.Completed, AuditPhase.Finished,
                BaseTime, BaseTime.AddHours(1),
                $$"""{"fetched":{{UrlCount}},"findings":{{FindingCount}}}""",
                "scale-bench-1.0", false), cancellationToken);

        var provider = _services.GetRequiredService<IDbProvider>();
        await using var connection = await provider.OpenConnectionAsync(cancellationToken);

        // The concrete SqlRepository is exactly what DI registers for every
        // IWriteRepository<T>; constructing it here unlocks the shared-transaction
        // CreateAsync overload for bounded batched ingestion.
        var urlIds = new long[UrlCount];
        var urls = await InsertBatchedAsync(
            new SqlRepository<UrlRecordDto>(provider),
            connection, UrlRows(random, audit.Id),
            (created, index) => urlIds[index] = created.Id,
            cancellationToken);

        var findings = await InsertBatchedAsync(
            new SqlRepository<FindingDto>(provider),
            connection, FindingRows(random, audit.Id, urlIds), null, cancellationToken);

        var links = await InsertBatchedAsync(
            new SqlRepository<LinkEdgeDto>(provider),
            connection, LinkRows(random, audit.Id, urlIds), null, cancellationToken);

        var extraction = await InsertBatchedAsync(
            new SqlRepository<ExtractedContentDto>(provider),
            connection, ExtractionRows(random, audit.Id, urlIds), null, cancellationToken);

        var ngrams = await InsertBatchedAsync(
            new SqlRepository<NgramFrequencyDto>(provider),
            connection, NgramRows(random, audit.Id, urlIds), null, cancellationToken);

        var siteSummaries = await InsertBatchedAsync(
            new SqlRepository<NgramSiteSummaryDto>(provider),
            connection, SiteSummaryRows(random, audit.Id), null, cancellationToken);

        var metrics = await InsertBatchedAsync(
            new SqlRepository<MetricSummaryDto>(provider),
            connection, MetricRows(audit.Id), null, cancellationToken);

        stopwatch.Stop();
        return new DatasetSummary(project.Id, configuration.Id, audit.Id,
            urls, findings, links, extraction, ngrams, siteSummaries, metrics, stopwatch.Elapsed.TotalSeconds);
    }

    private static IEnumerable<UrlRecordDto> UrlRows(Random random, long auditId)
    {
        for (var i = 0; i < UrlCount; i++)
        {
            var bucket = i % 100;
            var (statusClass, statusCode) = bucket switch
            {
                < 78 => (CrawlStatusClass.Success, 200),
                < 86 => (CrawlStatusClass.Redirect, 301),
                < 95 => (CrawlStatusClass.ClientError, 404),
                < 98 => (CrawlStatusClass.ServerError, 503),
                _ => (CrawlStatusClass.Blocked, 403)
            };
            var depth = i % 7;
            var host = $"site-{i % HostPoolSize}.example.test";
            var indexable = statusClass == CrawlStatusClass.Success && depth <= 4 && i % 17 != 0;
            var flags = indexable ? IndexabilityFlags.None
                : (i % 17 == 0 ? IndexabilityFlags.Noindex : IndexabilityFlags.NonIndexableStatus);
            var blocked = statusClass == CrawlStatusClass.Blocked;

            yield return new UrlRecordDto(
                0, auditId,
                $"https://{host}/section-{i % 40}/page-{i}",
                host,
                $"/section-{i % 40}/page-{i}",
                depth,
                i < HostPoolSize ? DiscoverySource.Seed : DiscoverySource.Link,
                statusClass,
                statusCode,
                statusClass == CrawlStatusClass.Redirect ? $"https://{host}/section-{i % 40}/page-{i + 1}" : null,
                statusClass is CrawlStatusClass.Success or CrawlStatusClass.Redirect ? "text/html" : null,
                statusClass == CrawlStatusClass.Success ? 4_096 + random.Next(32_768) : null,
                JsonSerializer.Serialize(new
                {
                    ttfbMs = 20 + random.Next(400),
                    totalMs = 60 + random.Next(900)
                }),
                blocked ? RobotsState.Disallowed : RobotsState.Allowed,
                flags,
                indexable)
            {
                BlockReason = blocked ? "waf-challenge" : null,
                WafProvider = blocked ? (WafProvider)(1 + i % 6) : null,
                ChallengeClass = blocked ? (WafChallengeClass)(1 + i % 3) : null
            };
        }
    }

    private static IEnumerable<FindingDto> FindingRows(Random random, long auditId, long[] urlIds)
    {
        for (var i = 0; i < FindingCount; i++)
        {
            var severity = (FindingSeverity)(i % 4);
            yield return new FindingDto(
                0, auditId,
                RuleKeys[i % RuleKeys.Length],
                (AuditCategory)(i % 11),
                severity,
                urlIds[(int)((long)i * 3 % UrlCount)],
                JsonSerializer.Serialize(new
                {
                    snippet = $"<element data-bench=\"{i}\">…</element>",
                    selector = $"main > section:nth-child({1 + i % 9})",
                    details = new { observed = random.Next(100), expected = random.Next(100) }
                }),
                null,
                false);
        }
    }

    private static IEnumerable<LinkEdgeDto> LinkRows(Random random, long auditId, long[] urlIds)
    {
        for (var i = 0; i < LinkCount; i++)
        {
            var sourceIndex = i % UrlCount;
            var targetIndex = (int)(((long)i * 7 + 13) % UrlCount);
            var external = i % 25 == 0;
            yield return new LinkEdgeDto(
                0, auditId,
                urlIds[sourceIndex],
                external
                    ? $"https://external-{i % 50}.example.test/resource-{i}"
                    : $"https://site-{targetIndex % HostPoolSize}.example.test/section-{targetIndex % 40}/page-{targetIndex}",
                external ? null : urlIds[targetIndex],
                $"anchor text {i % 500}",
                i % 13 == 0 ? LinkRelationFlags.Nofollow : LinkRelationFlags.None,
                !external)
            {
                DomZone = (DomZone)(i % 7),
                ZoneEvidence = $"zone-classifier:v1:score={0.5 + (i % 50) / 100.0:F2}",
                WeightedEquity = external ? null : Math.Round(1.0 / (1 + i % 9), 4)
            };
        }
    }

    private static IEnumerable<ExtractedContentDto> ExtractionRows(Random random, long auditId, long[] urlIds)
    {
        for (var i = 0; i < ExtractionCount; i++)
        {
            var ruleIndex = i % ExtractionRules.Length;
            var multiValued = i % 10 == 0;
            object value = ruleIndex switch
            {
                0 => Math.Round(random.NextDouble() * 999, 2),
                1 => i % 2 == 0,
                _ => 1 + i % 5
            };
            yield return new ExtractedContentDto(
                0, auditId,
                urlIds[(int)((long)i * 11 % UrlCount)],
                ExtractionRules[ruleIndex],
                multiValued ? i % 3 : 0,
                JsonSerializer.Serialize(value),
                ruleIndex == 1 ? ExtractedValueType.Boolean : ExtractedValueType.Number,
                false,
                false);
        }
    }

    private static IEnumerable<NgramFrequencyDto> NgramRows(Random random, long auditId, long[] urlIds)
    {
        for (var i = 0; i < NgramCount; i++)
        {
            var order = 1 + i % 3;
            var term = TermPool[i % TermPool.Length];
            for (var n = 1; n < order; n++)
            {
                term += " " + TermPool[(i + 7 * n) % TermPool.Length];
            }
            var count = 1 + random.Next(97);
            yield return new NgramFrequencyDto(
                0, auditId,
                urlIds[(int)((long)i * 13 % UrlCount)],
                (TextZone)(i % 4),
                order,
                term,
                count,
                Math.Round(count / 1000.0, 5));
        }
    }

    private static IEnumerable<NgramSiteSummaryDto> SiteSummaryRows(Random random, long auditId)
    {
        // The table enforces UNIQUE(audit_id, zone, ngram_order, term): terms
        // are built bijectively from the row index within each (zone, order)
        // class so 120k rows never collide.
        for (var i = 0; i < SiteSummaryCount; i++)
        {
            var zone = (TextZone)(i % 4);
            var order = 1 + i % 3;
            var t = i / 12;
            var term = order switch
            {
                1 => $"{TermPool[t % 40]}-{t / 40}",
                2 => $"{TermPool[t % 40]} {TermPool[(t / 40) % 40]}-{t / 1600}",
                _ => $"{TermPool[t % 40]} {TermPool[(t / 40) % 40]} {TermPool[(t / 1600) % 40]}"
            };
            var documentFrequency = 1 + random.Next(5_000);
            var totalFrequency = documentFrequency * (1 + random.Next(20));
            yield return new NgramSiteSummaryDto(
                0, auditId,
                zone,
                order,
                term,
                documentFrequency,
                totalFrequency,
                Math.Round((double)totalFrequency / documentFrequency, 5),
                random.Next(documentFrequency + 1));
        }
    }

    private static IEnumerable<MetricSummaryDto> MetricRows(long auditId)
    {
        var statusClasses = new (string Class, double Count)[]
        {
            ("2xx", 390_000), ("3xx", 40_000), ("4xx", 45_000), ("5xx", 15_000), ("blocked", 10_000)
        };
        var ordinal = 0;
        foreach (var (statusClass, count) in statusClasses)
        {
            yield return new MetricSummaryDto(0, auditId, "status-class-count",
                $$"""{"statusClass":"{{statusClass}}"}""", count);
            ordinal++;
        }
        for (var severity = 0; severity < 4; severity++)
        {
            yield return new MetricSummaryDto(0, auditId, "findings-by-severity",
                $$"""{"severity":{{severity}}}""", FindingCount / 4.0);
            ordinal++;
        }
        yield return new MetricSummaryDto(0, auditId, "indexable-rate", "{}", 0.62);
        yield return new MetricSummaryDto(0, auditId, "mean-ttfb-ms", "{}", 210);
        yield return new MetricSummaryDto(0, auditId, "crawl-depth-max", "{}", 6);
        yield return new MetricSummaryDto(0, auditId, "internal-link-count", "{}", LinkCount * 24 / 25);
        _ = ordinal;
    }

    /// <summary>
    /// Streams generated rows through the real repository inside shared
    /// transactions, committing every <see cref="CommitEvery"/> rows. Memory is
    /// bounded: one row in flight, one id array for cross-references.
    /// </summary>
    private static async Task<long> InsertBatchedAsync<TDto>(
        SqlRepository<TDto> repository,
        DbConnection connection,
        IEnumerable<TDto> rows,
        Action<TDto, int>? onCreated,
        CancellationToken cancellationToken)
        where TDto : class
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long total = 0;
            var pending = 0;
            var index = 0;
            foreach (var row in rows)
            {
                var created = await repository.CreateAsync(row, connection, transaction, cancellationToken);
                onCreated?.Invoke(created, index);
                index++;
                total++;
                pending++;
                if (pending >= CommitEvery)
                {
                    await transaction.CommitAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    transaction = await connection.BeginTransactionAsync(cancellationToken);
                    pending = 0;
                }
            }
            if (pending > 0)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return total;
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }
}
