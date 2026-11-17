using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.LinkGraph;

/// <summary>
/// Local options for <see cref="WeightedLinkEquityCalculator"/>.
/// </summary>
/// <param name="Damping">PageRank damping factor (0–1 exclusive); default 0.85.</param>
/// <param name="MaxIterations">Iteration cap; default 100.</param>
/// <param name="Tolerance">L1 convergence tolerance; default 1e-8.</param>
/// <param name="PersistenceBatchSize">Rows per transactional persistence batch; default 500.</param>
public sealed record WeightedEquityOptions(
    double Damping = 0.85,
    int MaxIterations = 100,
    double Tolerance = 1e-8,
    int PersistenceBatchSize = 500);

/// <summary>
/// Full equity computation outcome: weighted and unweighted distributions kept
/// side by side for comparability (FS-CRAWL-002 §15.2), plus per-URL percentiles
/// and site concentration.
/// </summary>
/// <param name="Weighted">Zone-weighted equity per URL.</param>
/// <param name="Unweighted">Unweighted equity per URL (comparability baseline).</param>
/// <param name="Percentiles">Weighted-equity percentile per URL (0–1).</param>
/// <param name="Concentration">Share of total weighted equity held by the top decile of URLs (0–1).</param>
public sealed record LinkEquityResult(
    IReadOnlyList<LinkEquityScore> Weighted,
    IReadOnlyList<LinkEquityScore> Unweighted,
    IReadOnlyDictionary<long, double> Percentiles,
    double Concentration);

/// <summary>
/// Weighted link-equity calculator (FS-CRAWL-002 §15.2): a weighted PageRank
/// variant over the internal link graph where each edge's transition weight is
/// its DOM-zone weight (in-content highest by default). The unweighted
/// distribution is computed in the same iteration for comparability. The graph
/// is built by streaming link_edges/url_records into compact identifier-indexed
/// arrays — bounded memory at 500k+ URLs. Dangling mass is redistributed
/// uniformly; iterations stop at L1 convergence or the configured cap.
/// </summary>
public sealed class WeightedLinkEquityCalculator : IWeightedLinkEquityCalculator
{
    /// <summary>
    /// Bundled default zone weights (FS-CRAWL-002 §15.2): in-content 1.0,
    /// breadcrumb 0.6, navigation 0.3, sidebar 0.3, header 0.2, footer 0.1.
    /// Residual Other links receive the lowest boilerplate weight (0.1).
    /// </summary>
    public static readonly IReadOnlyDictionary<DomZone, double> DefaultZoneWeights =
        new Dictionary<DomZone, double>
        {
            [DomZone.MainContent] = 1.0,
            [DomZone.Breadcrumb] = 0.6,
            [DomZone.Navigation] = 0.3,
            [DomZone.Sidebar] = 0.3,
            [DomZone.Header] = 0.2,
            [DomZone.Footer] = 0.1,
            [DomZone.Other] = 0.1
        };

    private readonly IDbProvider _provider;
    private readonly WeightedEquityOptions _options;

    /// <summary>Creates the calculator.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="options">Local options; null uses the documented defaults.</param>
    public WeightedLinkEquityCalculator(IDbProvider provider, WeightedEquityOptions? options = null)
    {
        _provider = provider;
        _options = options ?? new WeightedEquityOptions();
        if (_options.Damping is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Damping must be between 0 and 1 (exclusive).");
        }

        if (_options.MaxIterations < 1 || _options.Tolerance <= 0 || _options.PersistenceBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Iterations, tolerance, and batch size must be positive.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkEquityScore>> ComputeAsync(
        long auditId,
        IReadOnlyDictionary<DomZone, double> zoneWeights,
        CancellationToken cancellationToken = default)
    {
        var result = await ComputeFullAsync(auditId, zoneWeights, cancellationToken);
        return result.Weighted;
    }

    /// <summary>
    /// Computes weighted and unweighted distributions, percentiles, and concentration.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="zoneWeights">Per-zone weight multipliers; missing zones fall back to the bundled defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full equity outcome.</returns>
    public async Task<LinkEquityResult> ComputeFullAsync(
        long auditId,
        IReadOnlyDictionary<DomZone, double> zoneWeights,
        CancellationToken cancellationToken = default)
    {
        ValidateWeights(zoneWeights);

        // Stream URL identifiers into a compact index.
        var urlIds = new List<long>();
        var indexById = new Dictionary<long, int>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM url_records WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                indexById[id] = urlIds.Count;
                urlIds.Add(id);
            }
        }

        var nodeCount = urlIds.Count;
        if (nodeCount == 0)
        {
            return new LinkEquityResult([], [], new Dictionary<long, double>(), 0);
        }

        // Stream internal edges into compact arrays (self-loops excluded).
        var sources = new List<int>();
        var targets = new List<int>();
        var weights = new List<double>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT source_url_id, target_url_id, dom_zone FROM link_edges " +
                "WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NOT NULL ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceId = reader.GetInt64(0);
                var targetId = reader.GetInt64(1);
                if (sourceId == targetId ||
                    !indexById.TryGetValue(sourceId, out var source) ||
                    !indexById.TryGetValue(targetId, out var target))
                {
                    continue;
                }

                // Unclassified edges ride the in-content weight (documented default).
                var zone = reader.IsDBNull(2) ? DomZone.MainContent : (DomZone)reader.GetInt64(2);
                sources.Add(source);
                targets.Add(target);
                weights.Add(ResolveWeight(zoneWeights, zone));
            }
        }

        var edgeCount = sources.Count;
        var outWeightSum = new double[nodeCount];
        var outCount = new int[nodeCount];
        for (var e = 0; e < edgeCount; e++)
        {
            outWeightSum[sources[e]] += weights[e];
            outCount[sources[e]]++;
        }

        var weighted = IteratePageRank(nodeCount, edgeCount, sources, targets, weights, outWeightSum, outCount, weighted: true, cancellationToken);
        var unweighted = IteratePageRank(nodeCount, edgeCount, sources, targets, weights, outWeightSum, outCount, weighted: false, cancellationToken);

        var weightedScores = new List<LinkEquityScore>(nodeCount);
        var unweightedScores = new List<LinkEquityScore>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            weightedScores.Add(new LinkEquityScore(urlIds[i], weighted[i]));
            unweightedScores.Add(new LinkEquityScore(urlIds[i], unweighted[i]));
        }

        var percentiles = ComputePercentiles(urlIds, weighted);
        var concentration = ComputeTopDecileShare(weighted);
        return new LinkEquityResult(weightedScores, unweightedScores, percentiles, concentration);
    }

    /// <summary>
    /// Persists the outcome under metric_summaries: per-URL rows for the weighted
    /// key (spec-mandated) and the unweighted comparability key, dimensions
    /// <c>{"urlId":id,"percentile":p}</c>, plus one concentration summary row.
    /// Writes are batched transactionally (bounded memory).
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="result">The computation outcome.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all rows are durable.</returns>
    public async Task PersistAsync(long auditId, LinkEquityResult result, CancellationToken cancellationToken = default)
    {
        var rows = new List<MetricRow>();
        foreach (var score in result.Weighted)
        {
            rows.Add(new MetricRow(
                MetricKeys.LinkEquityWeighted,
                JsonSerializer.Serialize(new { urlId = score.UrlId, percentile = result.Percentiles.GetValueOrDefault(score.UrlId) }),
                score.Equity));
        }

        foreach (var score in result.Unweighted)
        {
            rows.Add(new MetricRow(
                LinkGraphMetricKeys.LinkEquityUnweighted,
                JsonSerializer.Serialize(new { urlId = score.UrlId }),
                score.Equity));
        }

        rows.Add(new MetricRow(
            LinkGraphMetricKeys.EquityConcentration,
            JsonSerializer.Serialize(new { scope = "top-decile" }),
            result.Concentration));

        for (var offset = 0; offset < rows.Count; offset += _options.PersistenceBatchSize)
        {
            var batch = rows.Skip(offset).Take(_options.PersistenceBatchSize);
            await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var row in batch)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO metric_summaries (audit_id, metric_key, dimensions_json, metric_value) " +
                    "VALUES (@audit, @key, @dimensions, @value)";
                AddParameter(command, "@audit", auditId);
                AddParameter(command, "@key", row.MetricKey);
                AddParameter(command, "@dimensions", row.DimensionsJson);
                AddParameter(command, "@value", row.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private double[] IteratePageRank(
        int nodeCount,
        int edgeCount,
        List<int> sources,
        List<int> targets,
        List<double> weights,
        double[] outWeightSum,
        int[] outCount,
        bool weighted,
        CancellationToken cancellationToken)
    {
        var scores = new double[nodeCount];
        var next = new double[nodeCount];
        var contributions = new double[nodeCount];
        var initial = 1.0 / nodeCount;
        Array.Fill(scores, initial);
        var damping = _options.Damping;

        for (var iteration = 0; iteration < _options.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double danglingMass = 0;
            for (var i = 0; i < nodeCount; i++)
            {
                if (outCount[i] == 0)
                {
                    danglingMass += scores[i];
                }
            }

            Array.Clear(contributions);
            for (var e = 0; e < edgeCount; e++)
            {
                var source = sources[e];
                var edgeWeight = weighted ? weights[e] : 1.0;
                var divisor = weighted ? outWeightSum[source] : outCount[source];
                contributions[targets[e]] += scores[source] * edgeWeight / divisor;
            }

            var baseRank = (1 - damping) / nodeCount + damping * danglingMass / nodeCount;
            var delta = 0.0;
            for (var i = 0; i < nodeCount; i++)
            {
                next[i] = baseRank + damping * contributions[i];
                delta += Math.Abs(next[i] - scores[i]);
            }

            (scores, next) = (next, scores);
            if (delta < _options.Tolerance)
            {
                break;
            }
        }

        return scores;
    }

    private static IReadOnlyDictionary<long, double> ComputePercentiles(List<long> urlIds, double[] scores)
    {
        var order = Enumerable.Range(0, scores.Length).ToArray();
        Array.Sort(order, (a, b) => scores[a].CompareTo(scores[b]));
        var percentiles = new Dictionary<long, double>(scores.Length);
        var denominator = Math.Max(1, scores.Length - 1);
        for (var rank = 0; rank < order.Length; rank++)
        {
            percentiles[urlIds[order[rank]]] = (double)rank / denominator;
        }

        return percentiles;
    }

    private static double ComputeTopDecileShare(double[] scores)
    {
        var total = scores.Sum();
        if (total <= 0)
        {
            return 0;
        }

        var topCount = Math.Max(1, (int)Math.Ceiling(scores.Length / 10.0));
        var topSum = scores.OrderByDescending(s => s).Take(topCount).Sum();
        return topSum / total;
    }

    private static double ResolveWeight(IReadOnlyDictionary<DomZone, double> zoneWeights, DomZone zone) =>
        zoneWeights.TryGetValue(zone, out var weight) ? weight : DefaultZoneWeights[zone];

    private static void ValidateWeights(IReadOnlyDictionary<DomZone, double> zoneWeights)
    {
        if (zoneWeights.Values.Any(w => w < 0 || double.IsNaN(w) || double.IsInfinity(w)))
        {
            throw new ArgumentException("Zone weights must be finite and non-negative.", nameof(zoneWeights));
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record MetricRow(string MetricKey, string DimensionsJson, double Value);
}
