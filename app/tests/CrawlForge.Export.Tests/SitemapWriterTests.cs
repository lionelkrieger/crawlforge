using System.IO.Compression;
using System.Xml.Linq;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Tests.TestInfrastructure;
using CrawlForge.Export.Writers;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Sitemap generator proofs (FS-CRAWL-001 §8.7): filtered URL set
/// (indexable + success), hints, home priority, XML escaping, and sitemap
/// index splitting above the per-file bound.
/// </summary>
public sealed class SitemapWriterTests
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>Only indexable, successfully crawled URLs enter the sitemap.</summary>
    [Fact]
    public async Task Sitemap_Filters_Indexable_Success_Urls()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/", path: "/");
        await stack.SeedUrlAsync(auditId, "https://example.com/noindex", indexable: false);
        await stack.SeedUrlAsync(auditId, "https://example.com/blocked", statusClass: CrawlStatusClass.Blocked);
        await stack.SeedUrlAsync(auditId, "https://example.com/a?x=1&y=2");

        var writer = new SitemapWriter(stack.Provider);
        Assert.Equal(2, await writer.CountEligibleUrlsAsync(auditId));

        await using var output = new MemoryStream();
        var count = await writer.WriteSingleAsync(output, auditId);
        Assert.Equal(2, count);

        var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        var locs = document.Descendants(Ns + "loc").Select(e => e.Value).ToList();
        Assert.Contains("https://example.com/", locs);
        Assert.Contains("https://example.com/a?x=1&y=2", locs); // escaped in XML, parsed back verbatim
        Assert.DoesNotContain("https://example.com/noindex", locs);
        Assert.DoesNotContain("https://example.com/blocked", locs);

        var home = document.Descendants(Ns + "url").Single(u => u.Element(Ns + "loc")!.Value == "https://example.com/");
        Assert.Equal("1.0", home.Element(Ns + "priority")!.Value);
        Assert.Equal("weekly", home.Element(Ns + "changefreq")!.Value);
        var other = document.Descendants(Ns + "url").First(u => u.Element(Ns + "loc")!.Value != "https://example.com/");
        Assert.Equal("0.5", other.Element(Ns + "priority")!.Value);
    }

    /// <summary>Above the per-file bound the output is an index plus split files.</summary>
    [Fact]
    public async Task Sitemap_Splits_Into_Index_Above_Bound()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        for (var i = 0; i < 5; i++)
        {
            await stack.SeedUrlAsync(auditId, $"https://example.com/page{i}");
        }

        var writer = new SitemapWriter(stack.Provider, new ExportOptions { SitemapMaxUrlsPerFile = 2 });
        await using var output = new MemoryStream();
        var written = await writer.WriteIndexAsync(output, auditId, "https://example.com");
        Assert.Equal(5, written);

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(4, archive.Entries.Count); // index + 3 sitemaps

        using var indexReader = new StreamReader(archive.GetEntry("sitemap-index.xml")!.Open());
        var index = XDocument.Parse(await indexReader.ReadToEndAsync());
        var indexLocs = index.Descendants(Ns + "loc").Select(e => e.Value).ToList();
        Assert.Equal(
            ["https://example.com/sitemap-1.xml", "https://example.com/sitemap-2.xml", "https://example.com/sitemap-3.xml"],
            indexLocs);

        var total = 0;
        foreach (var entry in archive.Entries.Where(e => e.Name.StartsWith("sitemap-", StringComparison.Ordinal) && e.Name != "sitemap-index.xml"))
        {
            using var reader = new StreamReader(entry.Open());
            var document = XDocument.Parse(await reader.ReadToEndAsync());
            var urls = document.Descendants(Ns + "url").Count();
            Assert.InRange(urls, 1, 2);
            total += urls;
        }

        Assert.Equal(5, total);
    }
}
