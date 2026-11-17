using System.Reflection;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations.Tests.Support;
using Xunit;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// Additive vocabulary coverage (FS-CRAWL-002 Appendix A): every element group,
/// metric key, and rule key from the locked contract constants round-trips
/// through the live schema, the discovery-source vocabulary gains list mode,
/// link edges carry all six DOM zones, and the additive snapshot, checkpoint,
/// and configuration payloads persist.
/// </summary>
public class AdditiveVocabularyTests : IAsyncLifetime
{
    private readonly MigrationTestDatabase _db = new();

    /// <summary>Creates, migrates, and seeds the parent graph of the per-test database.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.MigrateAsync();
        await TestGraph.InsertFullGraphAsync(_db);
    }

    /// <summary>Tears down the per-test database.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>Every additive element group constant round-trips through url_elements.</summary>
    [Fact]
    public async Task AllAdditiveElementGroupsRoundTrip()
    {
        var groups = ConstantValues(typeof(ElementGroups));
        Assert.NotEmpty(groups);
        foreach (var group in groups)
        {
            await _db.ExecuteAsync(
                $"INSERT INTO url_elements (url_id, element_group, payload_json, ordinal) VALUES (1, '{group}', '{{}}', 0);");
        }

        var stored = await DistinctStringsAsync("SELECT DISTINCT element_group FROM url_elements;");
        Assert.Equal(groups.OrderBy(g => g, StringComparer.Ordinal), stored);
    }

    /// <summary>Every additive metric key constant round-trips through metric_summaries.</summary>
    [Fact]
    public async Task AllAdditiveMetricKeysRoundTrip()
    {
        var keys = ConstantValues(typeof(MetricKeys));
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            await _db.ExecuteAsync(
                $"INSERT INTO metric_summaries (audit_id, metric_key, dimensions_json, metric_value) VALUES (1, '{key}', '{{}}', 1.0);");
        }

        var stored = await DistinctStringsAsync("SELECT DISTINCT metric_key FROM metric_summaries;");
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), stored);
    }

    /// <summary>Every additive finding rule key constant round-trips through findings.</summary>
    [Fact]
    public async Task AllAdditiveRuleKeysRoundTrip()
    {
        var keys = ConstantValues(typeof(FindingRuleKeys));
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            await _db.ExecuteAsync(
                $"INSERT INTO findings (audit_id, rule_key, category, severity, evidence_json, is_suppressed) VALUES (1, '{key}', 0, 0, '{{}}', 0);");
        }

        var stored = await DistinctStringsAsync("SELECT DISTINCT rule_key FROM findings;");
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), stored);
    }

    /// <summary>All audit categories persist, including the three additive categories.</summary>
    [Fact]
    public async Task AllAuditCategoriesPersist()
    {
        foreach (AuditCategory category in Enum.GetValues<AuditCategory>())
        {
            await _db.ExecuteAsync(
                $"INSERT INTO findings (audit_id, rule_key, category, severity, evidence_json, is_suppressed) VALUES (1, 'category-probe', {(int)category}, 0, '{{}}', 0);");
        }

        await using var connection = await _db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT category FROM findings WHERE rule_key = 'category-probe';";
        var stored = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            stored.Add(reader.GetInt32(0));
        }

        var expected = Enum.GetValues<AuditCategory>().Select(c => (int)c).OrderBy(v => v).ToList();
        Assert.Equal(expected, stored.OrderBy(v => v));
    }

    /// <summary>The discovery-source vocabulary accepts the additive list-mode value.</summary>
    [Fact]
    public async Task ListModeDiscoverySourcePersists()
    {
        await _db.ExecuteAsync(
            "INSERT INTO url_records (id, audit_id, canonical_url, host, path, depth, discovery_source, status_class, robots_state, indexability_flags, is_indexable) " +
            $"VALUES (3, 1, 'https://example.com/listed', 'example.com', '/listed', 0, {(int)DiscoverySource.List}, 0, 0, 0, 1);");
        var source = await _db.ScalarAsync("SELECT discovery_source FROM url_records WHERE id = 3;");
        Assert.Equal((int)DiscoverySource.List, Convert.ToInt32(source));
    }

    /// <summary>The additive block-reason and WAF fields persist on url_records.</summary>
    [Fact]
    public async Task WafBlockFieldsPersistOnUrlRecords()
    {
        await _db.ExecuteAsync(
            $"UPDATE url_records SET status_class = {(int)CrawlStatusClass.Blocked}, block_reason = 'challenge-page', " +
            $"waf_provider = {(int)WafProvider.Cloudflare}, challenge_class = {(int)WafChallengeClass.Interstitial} WHERE id = 1;");
        var reason = await _db.ScalarAsync("SELECT block_reason FROM url_records WHERE id = 1;");
        var provider = await _db.ScalarAsync("SELECT waf_provider FROM url_records WHERE id = 1;");
        var challenge = await _db.ScalarAsync("SELECT challenge_class FROM url_records WHERE id = 1;");
        Assert.Equal("challenge-page", reason);
        Assert.Equal((int)WafProvider.Cloudflare, Convert.ToInt32(provider));
        Assert.Equal((int)WafChallengeClass.Interstitial, Convert.ToInt32(challenge));
    }

    /// <summary>Every contract DOM zone persists on link_edges with zone evidence and weighted equity.</summary>
    [Fact]
    public async Task AllDomZonesPersistOnLinkEdges()
    {
        foreach (DomZone zone in Enum.GetValues<DomZone>())
        {
            await _db.ExecuteAsync(
                "INSERT INTO link_edges (audit_id, source_url_id, canonical_target_url, target_url_id, relation_flags, is_internal, dom_zone, zone_evidence, weighted_equity) " +
                $"VALUES (1, 1, 'https://example.com/page-2', 2, 0, 1, {(int)zone}, 'semantic-region', 0.5);");
        }

        await using var connection = await _db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT dom_zone FROM link_edges;";
        var stored = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            stored.Add(reader.GetInt32(0));
        }

        // Contract-driven count: the locked DomZone vocabulary grew to seven
        // zones (WP-03-A1 added Breadcrumb); never hardcode the zone count.
        Assert.Equal(Enum.GetValues<DomZone>().Length, stored.Count);
        Assert.Equal(Enum.GetValues<DomZone>().Select(z => (int)z).OrderBy(v => v), stored.OrderBy(v => v));
        var evidence = await _db.ScalarAsync("SELECT zone_evidence FROM link_edges LIMIT 1;");
        Assert.Equal("semantic-region", evidence);
    }

    /// <summary>The additive AIMD controller payload persists on crawl_checkpoints.</summary>
    [Fact]
    public async Task AimdCheckpointPayloadPersists()
    {
        const string payload = "{\"Hosts\":[{\"Host\":\"example.com\",\"CurrentRate\":1.5}]}";
        await _db.ExecuteAsync(
            "INSERT INTO crawl_checkpoints (audit_id, serialized_frontier, counters_json, saved_at, aimd_state_json) " +
            $"VALUES (1, '{{}}', '{{}}', '{TestGraph.Timestamp}', '{payload}');");
        var stored = await _db.ScalarAsync("SELECT aimd_state_json FROM crawl_checkpoints WHERE audit_id = 1;");
        Assert.Equal(payload, stored);
    }

    /// <summary>All additive audit snapshot fields persist.</summary>
    [Fact]
    public async Task AuditSnapshotFieldsPersist()
    {
        await _db.ExecuteAsync(
            "UPDATE audits SET waf_catalogue_version = '2026.1', waf_catalogue_revision = 'r3', " +
            "detection_policy_json = '{}', resolved_content_scope_json = '{}', is_list_mode = 1, " +
            "url_list_json = '[]', rewrite_rules_json = '[]', host_mapping_json = '{}', " +
            "redirect_matrix_json = '[]', embedding_provider = 0, embedding_model_tag = 'bundled-onnx', " +
            $"embedding_similarity_threshold = 0.82, agent_catalogue_version = '2026.1', geo_toggles_json = '{{}}' WHERE id = 1;");

        var catalogue = await _db.ScalarAsync("SELECT waf_catalogue_version FROM audits WHERE id = 1;");
        var listMode = await _db.ScalarAsync("SELECT is_list_mode FROM audits WHERE id = 1;");
        var threshold = await _db.ScalarAsync("SELECT embedding_similarity_threshold FROM audits WHERE id = 1;");
        var geoToggles = await _db.ScalarAsync("SELECT geo_toggles_json FROM audits WHERE id = 1;");
        Assert.Equal("2026.1", catalogue);
        Assert.Equal(1L, Convert.ToInt64(listMode));
        Assert.Equal(0.82, Convert.ToDouble(threshold));
        Assert.Equal("{}", geoToggles);
    }

    /// <summary>All additive configuration fields persist, including the profile reference.</summary>
    [Fact]
    public async Task ConfigurationExtensionFieldsPersist()
    {
        await _db.ExecuteAsync(
            "INSERT INTO content_scope_profiles (id, project_id, name, include_selectors_json, exclude_selectors_json, created_at, updated_at) " +
            $"VALUES (1, 1, 'Main content', '[\"main\"]', '[\"nav\"]', '{TestGraph.Timestamp}', '{TestGraph.Timestamp}');");
        await _db.ExecuteAsync(
            "UPDATE crawl_configurations SET content_scope_json = '{}', content_scope_profile_id = 1, is_list_mode = 1, " +
            "url_list_json = '[]', rewrite_rules_json = '[]', host_mapping_json = '{}', consent_mode_enabled = 1, " +
            "primary_container_override = 'GTM-TEST', data_layer_schema_json = '{}', beacon_patterns_json = '[]', " +
            "zone_weights_json = '{}', serp_budgets_json = '{}' WHERE id = 1;");

        var profile = await _db.ScalarAsync("SELECT content_scope_profile_id FROM crawl_configurations WHERE id = 1;");
        var container = await _db.ScalarAsync("SELECT primary_container_override FROM crawl_configurations WHERE id = 1;");
        var consent = await _db.ScalarAsync("SELECT consent_mode_enabled FROM crawl_configurations WHERE id = 1;");
        Assert.Equal(1L, Convert.ToInt64(profile));
        Assert.Equal("GTM-TEST", container);
        Assert.Equal(1L, Convert.ToInt64(consent));
    }

    /// <summary>Additive project-level fields persist.</summary>
    [Fact]
    public async Task ProjectAdditiveFieldsPersist()
    {
        await _db.ExecuteAsync(
            "UPDATE projects SET content_scope_json = '{}', rewrite_rules_json = '[]', host_mapping_json = '{}', " +
            "consent_mode_enabled = 1, beacon_patterns_json = '[]', zone_weights_json = '{}', serp_budgets_json = '{}' WHERE id = 1;");
        var consent = await _db.ScalarAsync("SELECT consent_mode_enabled FROM projects WHERE id = 1;");
        Assert.Equal(1L, Convert.ToInt64(consent));
    }

    private static List<string> ConstantValues(Type constantsType) =>
        constantsType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private async Task<List<string>> DistinctStringsAsync(string sql)
    {
        await using var connection = await _db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.OrderBy(v => v, StringComparer.Ordinal).ToList();
    }
}
