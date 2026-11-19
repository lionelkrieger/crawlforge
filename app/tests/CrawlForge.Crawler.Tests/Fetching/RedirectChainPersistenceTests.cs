using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using CrawlForge.Crawler.Tests.Frontier.Support;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Hop-by-hop redirect-chain persistence (G27 reopen, FS-CRAWL-001 §5.4/§5.8):
/// recorder persistAsync hook → store, the url_elements "redirect-chain"
/// mechanism over the locked repositories (in-memory doubles AND the real
/// SQLite stack), canonical JSON shapes, and thread safety.
/// </summary>
public sealed class RedirectChainPersistenceTests
{
    [Fact]
    public async Task Recorder_WithPersistHook_PersistsFullChainAfterEveryHop()
    {
        var store = new InMemoryRedirectChainStore();
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));

        await recorder.RecordHopAsync(1, 50, new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301));
        RedirectChainPayload? afterFirst = await store.GetPayloadAsync(50);
        Assert.Single(afterFirst!.Hops);

        await recorder.RecordHopAsync(1, 50, new RedirectHop(1, "http://site.example/b", "http://site.example/c", 302));
        RedirectChainPayload? afterSecond = await store.GetPayloadAsync(50);
        Assert.Equal(2, afterSecond!.Hops.Count);
        Assert.Equal(2, store.SaveCalls);
        Assert.Equal("http://site.example/a", afterSecond.Hops[0].Url);
        Assert.Equal("http://site.example/c", afterSecond.Hops[1].ToUrl);
    }

    [Fact]
    public async Task Recorder_OutOfOrderHops_PersistedInOrdinalOrder()
    {
        var store = new InMemoryRedirectChainStore();
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));

        await recorder.RecordHopAsync(1, 51, new RedirectHop(1, "http://site.example/b", "http://site.example/c", 302));
        await recorder.RecordHopAsync(1, 51, new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301));

        RedirectChainPayload? payload = await store.GetPayloadAsync(51);
        Assert.Equal(new[] { 0, 1 }, payload!.Hops.Select(h => h.Ordinal));
    }

    [Fact]
    public void PayloadJson_HasCanonicalCamelCaseShape()
    {
        var payload = RedirectChainPersistence.BuildOutcome(
            new[] { new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301) },
            "http://site.example/b", 200);

        var json = RedirectChainJson.SerializePayload(payload);

        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("loopDetected").GetBoolean());
        Assert.False(root.GetProperty("chainCapExceeded").GetBoolean());
        Assert.Equal("http://site.example/b", root.GetProperty("finalUrl").GetString());
        Assert.Equal(200, root.GetProperty("finalStatusCode").GetInt32());
        JsonElement hop = root.GetProperty("hops")[0];
        Assert.Equal(0, hop.GetProperty("ordinal").GetInt32());
        Assert.Equal("http://site.example/a", hop.GetProperty("url").GetString());
        Assert.Equal("http://site.example/b", hop.GetProperty("toUrl").GetString());
        Assert.Equal(301, hop.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public void BuildOutcome_ViolationFlags()
    {
        var chain = new[] { new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301) };

        Assert.True(RedirectChainPersistence.BuildOutcome(chain, null, null, RedirectChainViolation.Loop).LoopDetected);
        Assert.True(RedirectChainPersistence.BuildOutcome(chain, null, null, RedirectChainViolation.ChainCapExceeded).ChainCapExceeded);
        RedirectChainPayload clean = RedirectChainPersistence.BuildOutcome(chain, "http://site.example/b", 200);
        Assert.False(clean.LoopDetected);
        Assert.False(clean.ChainCapExceeded);
    }

    [Fact]
    public void DeserializePayload_ToleratesAbsentAndMalformed()
    {
        Assert.Null(RedirectChainJson.DeserializePayload(null));
        Assert.Null(RedirectChainJson.DeserializePayload(""));
        Assert.Null(RedirectChainJson.DeserializePayload("{not json"));
    }

    [Fact]
    public void SnapshotPayloadRoundtrip_PreservesHops()
    {
        var snapshot = new RedirectChainSnapshot(1, 60, new[]
        {
            new RedirectHop(1, "http://site.example/b", "http://site.example/c", 302),
            new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301),
        });

        RedirectChainPayload payload = RedirectChainJson.FromSnapshot(snapshot);
        IReadOnlyList<RedirectHop> hops = RedirectChainJson.ToHops(payload);

        Assert.Equal(new[] { 0, 1 }, hops.Select(h => h.Ordinal));
        Assert.Equal("http://site.example/a", hops[0].FromUrl);
        Assert.Equal("http://site.example/c", hops[1].ToUrl);
        Assert.Equal(302, hops[1].StatusCode);
    }

    [Fact]
    public async Task UrlElementStore_InMemoryRepositories_UpsertsSingleRow()
    {
        var repositories = new InMemoryUrlElementRepositories();
        var store = new UrlElementRedirectChainStore(repositories, repositories);
        var snapshot = new RedirectChainSnapshot(1, 70, new[] { new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301) });

        await store.SaveChainAsync(snapshot);
        await store.SaveOutcomeAsync(70, RedirectChainPersistence.BuildOutcome(snapshot.Hops, "http://site.example/b", 200));

        Assert.Equal(1, repositories.RowCount);
        RedirectChainPayload? payload = await store.GetPayloadAsync(70);
        Assert.Equal("http://site.example/b", payload!.FinalUrl);
        Assert.Single(payload.Hops);

        UrlElementDto row = (await repositories.ListAsync(new ListQuery(0, 10,
            new Dictionary<string, string> { ["urlId"] = "70" }))).Items.Single();
        Assert.Equal(RedirectChainElementGroup.GroupName, row.ElementGroup);
        Assert.Equal(0, row.Ordinal);
        Assert.Null(await store.GetPayloadAsync(999));
    }

    [Fact]
    public async Task UrlElementStore_RealSqliteStack_PersistsHopByHopAndReadsBack()
    {
        await using var database = await FrontierTestDatabase.CreateAsync();
        await database.SeedAuditAsync(1);
        var urlId = await database.InsertUrlAsync(1, "http://site.example/a");
        var reads = new SqlRepository<UrlElementDto>(database.Provider);
        var writes = new SqlRepository<UrlElementDto>(database.Provider);
        var store = new UrlElementRedirectChainStore(reads, writes);
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));

        await recorder.RecordHopAsync(1, urlId, new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301));
        await recorder.RecordHopAsync(1, urlId, new RedirectHop(1, "http://site.example/b", "http://site.example/c", 302));
        await store.SaveOutcomeAsync(urlId, RedirectChainPersistence.BuildOutcome(
            await recorder.GetChainAsync(1, urlId), "http://site.example/c", 200));

        // Storage producer → repository consumer: exactly one redirect-chain row.
        var rows = await reads.ListAsync(new ListQuery(0, 10, new Dictionary<string, string>
        {
            ["urlId"] = urlId.ToString(),
            ["elementGroup"] = RedirectChainElementGroup.GroupName,
        }));
        UrlElementDto row = Assert.Single(rows.Items);
        Assert.Equal(0, row.Ordinal);

        RedirectChainPayload? payload = await store.GetPayloadAsync(urlId);
        Assert.Equal(2, payload!.Hops.Count);
        Assert.Equal("http://site.example/c", payload.FinalUrl);
        Assert.Equal(200, payload.FinalStatusCode);
        Assert.False(payload.LoopDetected);
        Assert.Equal(new[] { 301, 302 }, payload.Hops.Select(h => h.StatusCode));
    }

    [Fact]
    public async Task UrlElementStore_ConcurrentUrls_ThreadSafe()
    {
        var repositories = new InMemoryUrlElementRepositories();
        var store = new UrlElementRedirectChainStore(repositories, repositories);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));
            for (var hop = 0; hop < 4; hop++)
            {
                await recorder.RecordHopAsync(1, 1000 + i, new RedirectHop(hop, $"http://site.example/{i}/{hop}", $"http://site.example/{i}/{hop + 1}", 301));
            }
        })));

        Assert.Equal(16, repositories.RowCount);
        for (var i = 0; i < 16; i++)
        {
            RedirectChainPayload? payload = await store.GetPayloadAsync(1000 + i);
            Assert.Equal(4, payload!.Hops.Count);
        }
    }
}
