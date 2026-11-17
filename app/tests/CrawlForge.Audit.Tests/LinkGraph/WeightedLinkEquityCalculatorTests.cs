using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Tests.LinkGraph.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.LinkGraph;

/// <summary>
/// Weighted link equity (FS-CRAWL-002 §15.2, criterion #48 zone-weighting facet):
/// zone-weighted PageRank must differ from unweighted per configuration,
/// in-content ranks highest, defaults match the spec, and results persist under
/// the mandated metric keys.
/// </summary>
public sealed class WeightedLinkEquityCalculatorTests : IAsyncLifetime
{
    private LinkGraphTestDatabase _database = null!;
    private WeightedLinkEquityCalculator _calculator = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = await LinkGraphTestDatabase.CreateAsync();
        _calculator = new WeightedLinkEquityCalculator(_database.Provider);
        await _database.SeedAuditAsync(300);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _database.DisposeAsync();

    private async Task<(long Hub, long X, long Y)> SeedHubGraphAsync(DomZone xZone, DomZone yZone)
    {
        var hub = await _database.InsertUrlAsync(300, "https://example.com/hub", source: DiscoverySource.Seed);
        var x = await _database.InsertUrlAsync(300, "https://example.com/x");
        var y = await _database.InsertUrlAsync(300, "https://example.com/y");
        await _database.InsertEdgeAsync(300, hub, x, "https://example.com/x", xZone, "zone x");
        await _database.InsertEdgeAsync(300, hub, y, "https://example.com/y", yZone, "zone y");
        return (hub, x, y);
    }

    private static double Score(IReadOnlyList<LinkEquityScore> scores, long urlId) =>
        scores.Single(s => s.UrlId == urlId).Equity;

    [Fact]
    public async Task WeightedEquityDiffersFromUnweightedUnderNonUniformZoneWeights()
    {
        var (_, x, y) = await SeedHubGraphAsync(DomZone.MainContent, DomZone.Footer);

        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        // Unweighted: symmetric targets receive identical equity.
        Assert.Equal(Score(result.Unweighted, x), Score(result.Unweighted, y), 10);
        // Weighted: in-content beats footer — the distributions must diverge.
        Assert.True(Score(result.Weighted, x) > Score(result.Weighted, y));
        Assert.NotEqual(Score(result.Weighted, x), Score(result.Unweighted, x), 10);
    }

    [Fact]
    public async Task InContentLinksCarryTheHighestDefaultWeight()
    {
        var (_, x, y) = await SeedHubGraphAsync(DomZone.MainContent, DomZone.Navigation);

        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        Assert.True(Score(result.Weighted, x) > Score(result.Weighted, y));
    }

    [Fact]
    public void DefaultZoneWeightsMatchTheSpecification()
    {
        var weights = WeightedLinkEquityCalculator.DefaultZoneWeights;

        Assert.Equal(1.0, weights[DomZone.MainContent]);
        Assert.Equal(0.6, weights[DomZone.Breadcrumb]);
        Assert.Equal(0.3, weights[DomZone.Navigation]);
        Assert.Equal(0.3, weights[DomZone.Sidebar]);
        Assert.Equal(0.2, weights[DomZone.Header]);
        Assert.Equal(0.1, weights[DomZone.Footer]);
        Assert.True(weights[DomZone.MainContent] > weights.Values.Where(w => w != 1.0).Max());
    }

    [Fact]
    public async Task BreadcrumbZoneIsExpressibleWithItsOwnWeight()
    {
        // WP-03-A1 amendment proof: breadcrumb 0.6 sits between in-content and nav.
        var (_, x, y) = await SeedHubGraphAsync(DomZone.Breadcrumb, DomZone.Navigation);

        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        Assert.True(Score(result.Weighted, x) > Score(result.Weighted, y));
    }

    [Fact]
    public async Task UniformWeightsReproduceTheUnweightedDistribution()
    {
        var (_, x, y) = await SeedHubGraphAsync(DomZone.MainContent, DomZone.Footer);
        var uniform = Enum.GetValues<DomZone>().ToDictionary(z => z, _ => 1.0);

        var result = await _calculator.ComputeFullAsync(300, uniform);

        Assert.Equal(Score(result.Unweighted, x), Score(result.Weighted, x), 10);
        Assert.Equal(Score(result.Unweighted, y), Score(result.Weighted, y), 10);
    }

    [Fact]
    public async Task CustomZoneWeightsChangeTheDistribution()
    {
        var (_, x, y) = await SeedHubGraphAsync(DomZone.MainContent, DomZone.Footer);
        var custom = new Dictionary<DomZone, double>
        {
            [DomZone.MainContent] = 5.0,
            [DomZone.Footer] = 1.0
        };

        var customResult = await _calculator.ComputeFullAsync(300, custom);
        var defaultResult = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        // In-content preference persists under the custom configuration...
        var ratio = Score(customResult.Weighted, x) / Score(customResult.Weighted, y);
        Assert.True(ratio > 1.0, $"Expected in-content preference, ratio was {ratio}.");
        // ...and the distribution demonstrably differs from the default configuration.
        Assert.NotEqual(Score(defaultResult.Weighted, x), Score(customResult.Weighted, x), 8);
        Assert.NotEqual(Score(defaultResult.Weighted, y), Score(customResult.Weighted, y), 8);
    }

    [Fact]
    public async Task DistributionsSumToOneEvenWithDanglingNodes()
    {
        await SeedHubGraphAsync(DomZone.MainContent, DomZone.Footer);

        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        Assert.Equal(1.0, result.Weighted.Sum(s => s.Equity), 6);
        Assert.Equal(1.0, result.Unweighted.Sum(s => s.Equity), 6);
    }

    [Fact]
    public async Task PercentilesAndConcentrationAreWellFormed()
    {
        var (hub, x, y) = await SeedHubGraphAsync(DomZone.MainContent, DomZone.Footer);

        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        Assert.Equal(3, result.Percentiles.Count);
        Assert.All(result.Percentiles.Values, p => Assert.InRange(p, 0.0, 1.0));
        Assert.Equal(1.0, result.Percentiles.Values.Max(), 10);
        Assert.InRange(result.Concentration, 0.0, 1.0);
        // Top decile of 3 URLs is one URL: concentration equals the max share.
        var total = result.Weighted.Sum(s => s.Equity);
        var maxShare = result.Weighted.Max(s => s.Equity) / total;
        Assert.Equal(maxShare, result.Concentration, 10);
        Assert.Contains(hub, result.Percentiles.Keys);
        Assert.Contains(x, result.Percentiles.Keys);
        Assert.Contains(y, result.Percentiles.Keys);
    }

    [Fact]
    public async Task PersistAsyncWritesWeightedUnweightedAndConcentrationRows()
    {
        var (_, x, _) = await SeedHubGraphAsync(DomZone.MainContent, DomZone.Footer);
        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        await _calculator.PersistAsync(300, result);

        await using var connection = await _database.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT metric_key, dimensions_json, metric_value FROM metric_summaries WHERE audit_id = 300";
        var rows = new List<(string Key, string Dimensions, double Value)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
        }

        var weighted = rows.Where(r => r.Key == MetricKeys.LinkEquityWeighted).ToList();
        var unweighted = rows.Where(r => r.Key == LinkGraphMetricKeys.LinkEquityUnweighted).ToList();
        var concentration = rows.Where(r => r.Key == LinkGraphMetricKeys.EquityConcentration).ToList();
        Assert.Equal(3, weighted.Count);
        Assert.Equal(3, unweighted.Count);
        Assert.Single(concentration);
        Assert.All(weighted, r => Assert.Contains("\"urlId\"", r.Dimensions));
        Assert.All(weighted, r => Assert.Contains("\"percentile\"", r.Dimensions));
        Assert.Contains("\"scope\":\"top-decile\"", concentration[0].Dimensions);
        var xRow = weighted.Single(r => r.Dimensions.Contains($"\"urlId\":{x}"));
        Assert.Equal(Score(result.Weighted, x), xRow.Value, 10);
    }

    [Fact]
    public async Task ExternalEdgesAndSelfLoopsDoNotTransferEquity()
    {
        var hub = await _database.InsertUrlAsync(300, "https://example.com/hub", source: DiscoverySource.Seed);
        var x = await _database.InsertUrlAsync(300, "https://example.com/x");
        await _database.InsertEdgeAsync(300, hub, null, "https://external.example.org/", isInternal: false);
        await _database.InsertEdgeAsync(300, hub, hub, "https://example.com/hub", DomZone.MainContent);
        await _database.InsertEdgeAsync(300, hub, x, "https://example.com/x", DomZone.MainContent);

        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        // X receives all of the hub's outgoing weight.
        Assert.True(Score(result.Weighted, x) > Score(result.Weighted, hub));
        Assert.Equal(1.0, result.Weighted.Sum(s => s.Equity), 6);
    }

    [Fact]
    public async Task EmptyAuditProducesEmptyResults()
    {
        var result = await _calculator.ComputeFullAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        Assert.Empty(result.Weighted);
        Assert.Empty(result.Unweighted);
        Assert.Empty(result.Percentiles);
        Assert.Equal(0, result.Concentration);
    }

    [Fact]
    public async Task ContractSurfaceReturnsWeightedScores()
    {
        var (_, x, _) = await SeedHubGraphAsync(DomZone.MainContent, DomZone.Footer);
        IWeightedLinkEquityCalculator contract = _calculator;

        var scores = await contract.ComputeAsync(300, WeightedLinkEquityCalculator.DefaultZoneWeights);

        Assert.Equal(3, scores.Count);
        Assert.True(Score(scores, x) > 0);
    }

    [Fact]
    public void NegativeOrNonFiniteWeightsAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            _calculator.ComputeFullAsync(300, new Dictionary<DomZone, double> { [DomZone.Footer] = -0.1 }).GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() =>
            _calculator.ComputeFullAsync(300, new Dictionary<DomZone, double> { [DomZone.Footer] = double.NaN }).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    public void InvalidDampingIsRejected(double damping)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WeightedLinkEquityCalculator(_database.Provider, new WeightedEquityOptions(Damping: damping)));
    }
}
