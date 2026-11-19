using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// G10 component-side proof (CODEMAP §13 row 10): list-mode seeds are readable
/// from the persisted UrlListJson payload BEFORE the crawl starts. The JSON
/// shape is the string array written by AuditsController.StartAuditAsync
/// (:158); malformed entries surface as bounded diagnostics, never failures.
/// </summary>
public class ListSeedResolverTests
{
    [Fact]
    public void Resolve_StringArray_ReturnsValidatedSeedsInOrder()
    {
        var json = """["https://a.example/1","https://b.example/2"]""";

        var resolution = ListSeedResolver.Resolve(json, isListMode: true);

        Assert.True(resolution.IsListMode);
        Assert.Equal(["https://a.example/1", "https://b.example/2"], resolution.Urls);
        Assert.Equal(0, resolution.InvalidCount);
        Assert.Empty(resolution.Diagnostics);
    }

    [Fact]
    public void Resolve_MalformedEntries_DiagnosticsNeverFail()
    {
        var json = """["https://ok.example/1","not a url",42,"ftp://bad.example/x"]""";

        var resolution = ListSeedResolver.Resolve(json, isListMode: true);

        Assert.Equal(["https://ok.example/1"], resolution.Urls);
        Assert.Equal(3, resolution.InvalidCount);
        Assert.Equal(3, resolution.Diagnostics.Count);
        Assert.Equal(2, resolution.Diagnostics[0].LineNumber);
        Assert.Equal(3, resolution.Diagnostics[1].LineNumber);
        Assert.Equal(4, resolution.Diagnostics[2].LineNumber);
    }

    [Fact]
    public void Resolve_InvalidJson_SurfacesDiagnostic()
    {
        var resolution = ListSeedResolver.Resolve("{not json", isListMode: true);

        Assert.Empty(resolution.Urls);
        Assert.Equal(1, resolution.InvalidCount);
        Assert.Single(resolution.Diagnostics);
        Assert.True(resolution.IsListMode);
    }

    [Fact]
    public void Resolve_NonArrayJson_SurfacesDiagnostic()
    {
        var resolution = ListSeedResolver.Resolve("""{"urls":["https://a.example"]}""", isListMode: true);

        Assert.Empty(resolution.Urls);
        Assert.Equal(1, resolution.InvalidCount);
        Assert.Contains("array", resolution.Diagnostics[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_NullOrEmptyJson_IsEmptyResolution()
    {
        Assert.Empty(ListSeedResolver.Resolve(null, isListMode: true).Urls);
        Assert.Empty(ListSeedResolver.Resolve("  ", isListMode: false).Urls);
        Assert.False(ListSeedResolver.Resolve(null, isListMode: null).IsListMode);
    }

    [Fact]
    public void Resolve_TrimsWhitespaceAroundEntries()
    {
        var resolution = ListSeedResolver.Resolve("""["  https://a.example/1  "]""", isListMode: true);
        Assert.Equal(["https://a.example/1"], resolution.Urls);
    }

    [Fact]
    public async Task ResolveForAudit_ReadsPersistedList_BeforeStart()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();

        // Persist the list exactly as the audit-start surface does (G10: the
        // payload must be readable on its own, before any crawl work).
        await using (var connection = await fixture.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE `audits` SET `url_list_json` = @json, `is_list_mode` = 1 WHERE `id` = @auditId";
            var json = command.CreateParameter();
            json.ParameterName = "@json";
            json.Value = """["https://list.example/a","https://list.example/b"]""";
            command.Parameters.Add(json);
            var id = command.CreateParameter();
            id.ParameterName = "@auditId";
            id.Value = auditId;
            command.Parameters.Add(id);
            await command.ExecuteNonQueryAsync();
        }

        var resolution = await ListSeedResolver.ResolveForAuditAsync(fixture.Provider, auditId);

        Assert.True(resolution.IsListMode);
        Assert.Equal(["https://list.example/a", "https://list.example/b"], resolution.Urls);
        Assert.Equal(0, resolution.InvalidCount);
    }

    [Fact]
    public async Task ResolveForAudit_NonListAudit_IsEmpty()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();

        var resolution = await ListSeedResolver.ResolveForAuditAsync(fixture.Provider, auditId);

        Assert.False(resolution.IsListMode);
        Assert.Empty(resolution.Urls);
    }

    [Fact]
    public async Task ResolveForAudit_MissingAudit_IsEmpty()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var resolution = await ListSeedResolver.ResolveForAuditAsync(fixture.Provider, 999_999);

        Assert.False(resolution.IsListMode);
        Assert.Empty(resolution.Urls);
    }

    [Fact]
    public async Task ResolveForAudit_InvalidPersistedEntries_SurfaceDiagnostics()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();

        await using (var connection = await fixture.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE `audits` SET `url_list_json` = @json, `is_list_mode` = 1 WHERE `id` = @auditId";
            var json = command.CreateParameter();
            json.ParameterName = "@json";
            json.Value = """["https://ok.example/a","bogus"]""";
            command.Parameters.Add(json);
            var id = command.CreateParameter();
            id.ParameterName = "@auditId";
            id.Value = auditId;
            command.Parameters.Add(id);
            await command.ExecuteNonQueryAsync();
        }

        var resolution = await ListSeedResolver.ResolveForAuditAsync(fixture.Provider, auditId);

        Assert.Equal(["https://ok.example/a"], resolution.Urls);
        Assert.Equal(1, resolution.InvalidCount);
        Assert.Single(resolution.Diagnostics);
    }
}
