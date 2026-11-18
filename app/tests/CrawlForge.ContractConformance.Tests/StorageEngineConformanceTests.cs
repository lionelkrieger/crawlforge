using System.Globalization;
using System.Text.Json;
using CrawlForge.ContractConformance.Tests.Harness;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// Storage-to-engine conformance (FS-CRAWL-001 §9.2 + Appendix A) against the
/// harness's OWN scratch database: every catalog entity's physical layout
/// matches the locked DTO→table mapping (SchemaNames + EntityMap), enums/flags
/// store as INTEGER, timestamps round-trip TEXT, JSON payloads are valid JSON
/// TEXT, and the offline re-audit proves the engine reads stored artifacts
/// with the fixture site STOPPED (zero network).
/// </summary>
[Collection(LiveBackendCollection.Name)]
public sealed class StorageEngineConformanceTests
{
    private readonly LiveBackendFixture _fixture;

    /// <summary>Creates the suite over the shared live backend.</summary>
    /// <param name="fixture">Shared live-backend fixture.</param>
    public StorageEngineConformanceTests(LiveBackendFixture fixture) => _fixture = fixture;

    /// <summary>
    /// All 40 catalog entities exist as tables whose physical columns match
    /// the locked EntityMap layout (snake_case + SchemaNames overrides).
    /// </summary>
    [Fact]
    public void EveryCatalogEntity_TableExistsWithMappedColumns()
    {
        var tables = _fixture.Db.TableNames().ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var entity in SchemaNames.AllEntities)
        {
            var map = EntityMap.For(entity);
            if (!tables.Contains(map.TableName))
            {
                problems.Add($"{entity.Name}: table '{map.TableName}' missing");
                continue;
            }

            var physical = _fixture.Db.Columns(map.TableName).ToHashSet(StringComparer.Ordinal);
            foreach (var column in map.Columns)
            {
                if (!physical.Contains(column.ColumnName))
                {
                    problems.Add($"{map.TableName}: mapped column '{column.ColumnName}' ({column.PropertyName}) missing");
                }

                if (column.ColumnName != column.ColumnName.ToLowerInvariant())
                {
                    problems.Add($"{map.TableName}: column '{column.ColumnName}' is not snake_case");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Enums and boolean flags store as INTEGER 0/1 (precedence rule 6).</summary>
    [Fact]
    public void EnumAndFlagColumns_AreInteger()
    {
        // Physical types via PRAGMA.
        AssertColumnType("audits", "status", "INTEGER");
        AssertColumnType("audits", "phase", "INTEGER");
        AssertColumnType("url_records", "status_class", "INTEGER");
        AssertColumnType("url_records", "is_indexable", "INTEGER");
        AssertColumnType("url_records", "indexability_flags", "INTEGER");
        AssertColumnType("content_artifacts", "kind", "INTEGER");

        // Stored values are integers in range.
        var flagValues = _fixture.Db.Query(
            "SELECT DISTINCT is_indexable FROM url_records WHERE audit_id = @audit",
            reader => reader.GetInt64(0),
            new Dictionary<string, object?> { ["@audit"] = _fixture.AuditId });
        Assert.All(flagValues, value => Assert.True(value is 0 or 1, $"is_indexable stored {value}, expected 0/1"));

        var statusValues = _fixture.Db.Query(
            "SELECT DISTINCT status FROM audits",
            reader => reader.GetInt64(0));
        Assert.All(statusValues, value => Assert.True(Enum.IsDefined(typeof(AuditStatus), (int)value), $"audits.status stored undefined enum value {value}"));
    }

    /// <summary>UTC timestamps store as round-trip ("O") TEXT and parse back.</summary>
    [Fact]
    public void TimestampColumns_RoundTripText()
    {
        AssertColumnType("audits", "started_at", "TEXT");
        var startedAt = (string?)_fixture.Db.Scalar(
            "SELECT started_at FROM audits WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = _fixture.AuditId });
        Assert.False(string.IsNullOrEmpty(startedAt));
        var parsed = DateTimeOffset.Parse(startedAt!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(parsed, DateTimeOffset.Parse(parsed.ToString("O", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// <summary>Complex payloads store as valid JSON TEXT.</summary>
    [Fact]
    public void JsonColumns_AreValidJsonText()
    {
        var limitsJson = (string?)_fixture.Db.Scalar(
            "SELECT limit_overrides_json FROM crawl_configurations WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = _fixture.ConfigurationId });
        Assert.False(string.IsNullOrEmpty(limitsJson));
        using var limits = JsonDocument.Parse(limitsJson!);
        // Storage-side DTO JSON is PascalCase (PayloadJson defaults), unlike
        // the camelCase REST wire — verified convention, not drift.
        Assert.Equal(220, limits.RootElement.GetProperty("MaxUrls").GetInt32());

        var countersJson = (string?)_fixture.Db.Scalar(
            "SELECT counters_json FROM audits WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = _fixture.AuditId });
        Assert.False(string.IsNullOrEmpty(countersJson));
        using var counters = JsonDocument.Parse(countersJson!);
        Assert.Equal(JsonValueKind.Object, counters.RootElement.ValueKind);
    }

    /// <summary>The engine's stored counters carry the producer keys (WP-09-06 producer side).</summary>
    [Fact]
    public void CountersJson_EngineProducerKeys()
    {
        var countersJson = (string?)_fixture.Db.Scalar(
            "SELECT counters_json FROM audits WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = _fixture.AuditId });
        using var document = JsonDocument.Parse(countersJson!);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "analyzed", "discovered", "failed", "fetched" }, keys);
    }

    /// <summary>
    /// Storage-to-engine proof: the offline re-audit (refresh:false) of the
    /// MCP-started source audit ran with the fixture site STOPPED and completed
    /// over stored artifacts (FS-CRAWL-001 §7.2 stored-artifacts rule); the
    /// restarted fixture's recorder shows zero non-GET traffic for the suite.
    /// G-REAUDIT-SNAPSHOT: the source must be MCP-started — REST start
    /// overwrites the DTO snapshot with the wire shape and re-audit 500s
    /// (evidence: captured/gap-reaudit-rest-start-response.json).
    /// </summary>
    [Fact]
    public async Task OfflineReaudit_ReadsStoredArtifacts_ZeroNetwork()
    {
        var reaudit = await Rest.GetDataAsync(_fixture.Http, $"/api/audits/{_fixture.ReauditId}");
        Assert.Equal("completed", reaudit.GetProperty("state").GetString());

        // The offline pass re-analyzed the stored URL population.
        var sourceCount = (long)_fixture.Db.Scalar(
            "SELECT COUNT(*) FROM url_records WHERE audit_id = @id",
            new Dictionary<string, object?> { ["@id"] = _fixture.OfflineSourceAuditId })!;
        var reauditCount = (long)_fixture.Db.Scalar(
            "SELECT COUNT(*) FROM url_records WHERE audit_id = @id",
            new Dictionary<string, object?> { ["@id"] = _fixture.ReauditId })!;
        Assert.True(sourceCount > 0, "offline source audit must have stored URL records");
        Assert.Equal(sourceCount, reauditCount);

        // The re-audit ran while the fixture was down; the restarted fixture's
        // recorder (and every later suite fetch) must show zero non-GET hits.
        var requests = await _fixture.Fixture.RequestsAsync();
        var nonGet = requests.GetProperty("nonGet");
        Assert.Equal(0, nonGet.GetArrayLength());

        await _fixture.SaveCaptureAsync("storage-offline-reaudit-proof.json", JsonSerializer.SerializeToElement(new
        {
            sourceAuditId = _fixture.OfflineSourceAuditId,
            reauditId = _fixture.ReauditId,
            state = "completed",
            sourceUrlRecords = sourceCount,
            reauditUrlRecords = reauditCount,
            fixtureNonGetHits = 0
        }));
    }

    /// <summary>
    /// G27 shape lock: the redirect-chain trap stores a single final target
    /// (redirect_target_url), not hop-by-hop rows. The chain's intermediate
    /// hops (chain-2, chain-3) have no url_records rows. Shape-only assertion
    /// (hop-count semantics belong to cf-wp-08-04's fixture acceptance).
    /// </summary>
    [Fact]
    public async Task RedirectChain_FinalTargetOnly_G27()
    {
        var rows = _fixture.Db.Query(
            "SELECT canonical_url, redirect_target_url, final_status_code FROM url_records WHERE audit_id = @audit AND (canonical_url LIKE '%/redirect/chain-%' OR canonical_url LIKE '%/pages/redirect-target.html')",
            reader => new
            {
                CanonicalUrl = reader.GetString(0),
                RedirectTargetUrl = reader.IsDBNull(1) ? null : reader.GetString(1),
                FinalStatusCode = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2)
            },
            new Dictionary<string, object?> { ["@audit"] = _fixture.AuditId });

        Assert.True(rows.Count > 0, "redirect-chain trap must have stored URL records");
        // No intermediate hop rows (G27: chains are not persisted hop-by-hop).
        Assert.DoesNotContain(rows, r => r.CanonicalUrl.EndsWith("/redirect/chain-2", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.CanonicalUrl.EndsWith("/redirect/chain-3", StringComparison.Ordinal));

        // The chain entry (or its canonicalized landing row) points at the final target.
        var chainRow = rows.FirstOrDefault(r => r.CanonicalUrl.EndsWith("/redirect/chain-1", StringComparison.Ordinal));
        if (chainRow is not null)
        {
            Assert.Equal(FixtureSite.BaseUrl + "/pages/redirect-target.html", chainRow.RedirectTargetUrl);
        }
        else
        {
            Assert.Contains(rows, r => r.CanonicalUrl.EndsWith("/pages/redirect-target.html", StringComparison.Ordinal));
        }

        await _fixture.SaveCaptureAsync("storage-redirect-chain-g27.json", JsonSerializer.SerializeToElement(rows));
    }

    /// <summary>
    /// The noindex trap stores a fetched URL record with the indexability
    /// dimensions present (shape only). The flag VALUE is a semantics question
    /// (G-NOINDEX-FLAG-QUESTION: observed is_indexable=1 / indexability_flags=0
    /// despite meta robots noindex; cf-wp-08-04 owns trap semantics via the
    /// indexability.noindex finding) — recorded as evidence, not asserted.
    /// </summary>
    [Fact]
    public async Task Noindex_UrlRecord_Shape()
    {
        var row = _fixture.Db.Query(
            "SELECT canonical_url, final_status_code, is_indexable, indexability_flags FROM url_records WHERE audit_id = @audit AND canonical_url LIKE '%/pages/noindex.html'",
            reader => new
            {
                CanonicalUrl = reader.GetString(0),
                FinalStatusCode = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
                IsIndexable = reader.GetInt64(2),
                IndexabilityFlags = reader.GetInt64(3)
            },
            new Dictionary<string, object?> { ["@audit"] = _fixture.AuditId });
        var noindex = Assert.Single(row);
        Assert.Equal(200, noindex.FinalStatusCode);
        Assert.True(noindex.IsIndexable is 0 or 1, "is_indexable must be an INTEGER boolean");
        await _fixture.SaveCaptureAsync("storage-noindex-row.json", JsonSerializer.SerializeToElement(noindex));
    }

    /// <summary>
    /// G03 shape lock: with rendering disabled (renderMode none) and the
    /// renderer inert, no RenderedDom artifacts exist. The body artifacts of
    /// the http-mode crawl ARE stored (the offline re-audit reads them).
    /// </summary>
    [Fact]
    public async Task RenderedDomArtifacts_Absent_G03()
    {
        var renderedDom = (long)_fixture.Db.Scalar(
            "SELECT COUNT(*) FROM content_artifacts WHERE kind = @kind",
            new Dictionary<string, object?> { ["@kind"] = (int)ContentArtifactKind.RenderedDom })!;
        Assert.Equal(0, renderedDom);

        var bodies = (long)_fixture.Db.Scalar(
            "SELECT COUNT(*) FROM content_artifacts WHERE kind = @kind",
            new Dictionary<string, object?> { ["@kind"] = (int)ContentArtifactKind.Body })!;
        Assert.True(bodies > 0, "http-mode crawl must store body artifacts for offline re-analysis");
        await _fixture.SaveCaptureAsync("storage-artifact-kinds.json", JsonSerializer.SerializeToElement(new
        {
            renderedDom,
            bodies,
            note = "G03: renderer inert; renderMode none — no RenderedDom rows (recorded, not failed)"
        }));
    }

    private void AssertColumnType(string table, string column, string expectedType)
    {
        var type = _fixture.Db.Query(
            $"PRAGMA table_info(\"{table}\")",
            reader => new { Name = reader.GetString(1), Type = reader.GetString(2) })
            .FirstOrDefault(c => c.Name == column)?.Type;
        Assert.Equal(expectedType, type);
    }
}
