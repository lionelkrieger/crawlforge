using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Tests.TestInfrastructure;
using CrawlForge.Export.Writers;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Sitemap filter proofs (WP-09-10, CAP-H-X03 completion; §8.7 "filterable by
/// indexability and status"): the additive filter overloads widen or narrow
/// the URL set while the default stays byte-compatible with the baseline
/// behavior; index splitting honors the filter.
/// </summary>
public sealed class SitemapFilterTests
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private static async Task<(ExportTestStack Stack, long AuditId)> SeedAsync()
    {
        var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/", path: "/");
        await stack.SeedUrlAsync(auditId, "https://example.com/noindex", indexable: false);
        await stack.SeedUrlAsync(auditId, "https://example.com/redirect", statusClass: CrawlStatusClass.Redirect);
        await stack.SeedUrlAsync(auditId, "https://example.com/blocked", statusClass: CrawlStatusClass.Blocked);
        return (stack, auditId);
    }

    /// <summary>Default filter unchanged: indexable + success only.</summary>
    [Fact]
    public async Task Default_Filter_Matches_Baseline()
    {
        var (stack, auditId) = await SeedAsync();
        await using var _ = stack;
        var writer = new SitemapWriter(stack.Provider);

        Assert.Equal(1, await writer.CountEligibleUrlsAsync(auditId));
        Assert.Equal(1, await writer.CountEligibleUrlsAsync(auditId, SitemapFilter.Default));

        await using var output = new MemoryStream();
        await writer.WriteSingleAsync(output, auditId);
        var document = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        var loc = Assert.Single(document.Descendants(Ns + "loc")).Value;
        Assert.Equal("https://example.com/", loc);
    }

    /// <summary>IndexableOnly=false admits noindex pages (status filter still applies).</summary>
    [Fact]
    public async Task IndexableOnly_False_Admits_Noindex_Pages()
    {
        var (stack, auditId) = await SeedAsync();
        await using var _ = stack;
        var writer = new SitemapWriter(stack.Provider);
        var filter = new SitemapFilter(IndexableOnly: false);

        Assert.Equal(2, await writer.CountEligibleUrlsAsync(auditId, filter));
        await using var output = new MemoryStream();
        await writer.WriteSingleAsync(output, auditId, filter);
        var document = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        var locs = document.Descendants(Ns + "loc").Select(e => e.Value).ToList();
        Assert.Contains("https://example.com/noindex", locs);
        Assert.DoesNotContain("https://example.com/redirect", locs);
    }

    /// <summary>Status classes widen the set (redirects included when asked).</summary>
    [Fact]
    public async Task StatusClasses_Filter_Widens_The_Url_Set()
    {
        var (stack, auditId) = await SeedAsync();
        await using var _ = stack;
        var writer = new SitemapWriter(stack.Provider);
        var filter = new SitemapFilter(StatusClasses: [CrawlStatusClass.Success, CrawlStatusClass.Redirect]);

        Assert.Equal(2, await writer.CountEligibleUrlsAsync(auditId, filter));
        await using var output = new MemoryStream();
        await writer.WriteSingleAsync(output, auditId, filter);
        var document = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        var locs = document.Descendants(Ns + "loc").Select(e => e.Value).ToList();
        Assert.Contains("https://example.com/redirect", locs);
        Assert.DoesNotContain("https://example.com/blocked", locs);
        Assert.DoesNotContain("https://example.com/noindex", locs); // still indexable-only
    }

    /// <summary>Combined filter + index split: every split file honors the filter.</summary>
    [Fact]
    public async Task Filtered_Index_Split_Stays_Consistent()
    {
        var (stack, auditId) = await SeedAsync();
        await using var _ = stack;
        var writer = new SitemapWriter(stack.Provider, new ExportOptions { SitemapMaxUrlsPerFile = 1 });
        var filter = new SitemapFilter(IndexableOnly: false, StatusClasses: [CrawlStatusClass.Success, CrawlStatusClass.Redirect, CrawlStatusClass.Blocked]);

        Assert.Equal(4, await writer.CountEligibleUrlsAsync(auditId, filter));
        await using var output = new MemoryStream();
        var written = await writer.WriteIndexAsync(output, auditId, "https://example.com", filter);
        Assert.Equal(4, written);

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(5, archive.Entries.Count); // index + 4 sitemaps
        var total = 0;
        foreach (var entry in archive.Entries.Where(e => e.Name != "sitemap-index.xml"))
        {
            using var reader = new StreamReader(entry.Open());
            var document = XDocument.Parse(await reader.ReadToEndAsync());
            total += document.Descendants(Ns + "url").Count();
        }

        Assert.Equal(4, total);
    }
}