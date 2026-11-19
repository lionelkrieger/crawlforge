using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Export.Tests.TestInfrastructure;
using CrawlForge.Export.Writers;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Full-page content export proofs (WP-09-10, CAP-G-164/165; §8.4 CR-017):
/// per-page plain text and Markdown bundles from the REAL compressed content
/// store over the real SQLite stack — parseable zip + manifest, stable
/// identifiers, rendered-DOM preference, and artifact-less pages skipped.
/// Fully offline.
/// </summary>
public sealed class PageContentWriterTests : IAsyncDisposable
{
    private const string PageHtml = """
        <!doctype html><html><head><title>Acme Page</title><script>var hidden = 1;</script><style>.x{color:red}</style></head>
        <body>
        <h1>Main Title</h1>
        <p>Hello <strong>world</strong> from the fixture.</p>
        <ul><li>first</li><li>second<ul><li>nested</li></ul></li></ul>
        <pre><code>line1
        line2</code></pre>
        <blockquote><p>quoted text</p></blockquote>
        <table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table>
        <img src="https://example.com/i.png" alt="pic"/>
        <hr/>
        </body></html>
        """;

    private readonly string _artifactRoot = Path.Combine(Path.GetTempPath(), "cf-page-export-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_artifactRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }

        return ValueTask.CompletedTask;
    }

    private static async Task PutPageAsync(CompressedContentStore store, long auditId, long urlId, string html, ContentArtifactKind kind = ContentArtifactKind.Body)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        await store.PutAsync(auditId, urlId, kind, stream);
    }

    private static async Task<Dictionary<string, string>> ReadBundleAsync(MemoryStream output)
    {
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            entries[entry.Name] = await reader.ReadToEndAsync();
        }

        return entries;
    }

    /// <summary>Plain-text bundle: one entry per stored page + identifier manifest.</summary>
    [Fact]
    public async Task PlainText_Writes_PerPage_Entries_With_Manifest()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        var url1 = await stack.SeedUrlAsync(auditId, "https://example.com/", path: "/");
        var url2 = await stack.SeedUrlAsync(auditId, "https://example.com/docs/intro", path: "/docs/intro");
        await stack.SeedUrlAsync(auditId, "https://example.com/no-artifact"); // skipped
        var store = new CompressedContentStore(_artifactRoot, stack.Provider);
        await PutPageAsync(store, auditId, url1, PageHtml);
        await PutPageAsync(store, auditId, url2, "<html><head><title>Intro</title></head><body><p>Intro body text.</p></body></html>");

        var writer = new PageContentExportWriter(stack.Provider, store, new AngleSharpDomParser());
        await using var output = new MemoryStream();
        var pages = await writer.WriteAsync(output, auditId, PageContentFormat.PlainText);

        Assert.Equal(2, pages);
        var bundle = await ReadBundleAsync(output);
        Assert.Equal(3, bundle.Count); // 2 pages + manifest
        Assert.Contains($"{url1}-page.txt", bundle.Keys);
        Assert.Contains($"{url2}-intro.txt", bundle.Keys);

        // Pure visible text: script/style content never leaks.
        var text = bundle[$"{url1}-page.txt"];
        Assert.Contains("Main Title", text);
        Assert.Contains("Hello", text);
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("color:red", text);

        // Manifest carries the §11.8 stable identifiers.
        using var manifest = JsonDocument.Parse(bundle["manifest.json"]);
        Assert.Equal("text", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("pageCount").GetInt32());
        var manifestPages = manifest.RootElement.GetProperty("pages").EnumerateArray().ToList();
        Assert.All(manifestPages, p => Assert.Equal(JsonValueKind.String, p.GetProperty("urlId").ValueKind));
        Assert.Contains(manifestPages, p => p.GetProperty("canonicalUrl").GetString() == "https://example.com/docs/intro");
    }

    /// <summary>Markdown bundle: title header, source line, converted content.</summary>
    [Fact]
    public async Task Markdown_Converts_Headings_Links_Lists_Code_Tables()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/page", path: "/page");
        var store = new CompressedContentStore(_artifactRoot, stack.Provider);
        await PutPageAsync(store, auditId, urlId, PageHtml);

        var writer = new PageContentExportWriter(stack.Provider, store, new AngleSharpDomParser());
        await using var output = new MemoryStream();
        var pages = await writer.WriteAsync(output, auditId, PageContentFormat.Markdown);

        Assert.Equal(1, pages);
        var bundle = await ReadBundleAsync(output);
        var markdown = bundle[$"{urlId}-page.md"];
        Assert.StartsWith("# Acme Page", markdown);
        Assert.Contains("> Source: https://example.com/page", markdown);
        Assert.Contains("# Main Title", markdown);
        Assert.Contains("**world**", markdown);
        Assert.Contains("- first", markdown);
        Assert.Contains("- nested", markdown);
        Assert.Contains("```", markdown);
        Assert.Contains("line1", markdown);
        Assert.Contains("> quoted text", markdown);
        Assert.Contains("| A | B |", markdown);
        Assert.Contains("| --- | --- |", markdown);
        Assert.Contains("| 1 | 2 |", markdown);
        Assert.Contains("![pic](https://example.com/i.png)", markdown);
        Assert.Contains("---", markdown);
        Assert.DoesNotContain("hidden", markdown);

        using var manifest = JsonDocument.Parse(bundle["manifest.json"]);
        Assert.Equal("markdown", manifest.RootElement.GetProperty("format").GetString());
    }

    /// <summary>The rendered DOM snapshot is preferred over the raw body.</summary>
    [Fact]
    public async Task RenderedDom_Is_Preferred_Over_RawBody()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/rendered");
        var store = new CompressedContentStore(_artifactRoot, stack.Provider);
        await PutPageAsync(store, auditId, urlId, "<html><body><p>raw body marker</p></body></html>");
        await PutPageAsync(store, auditId, urlId, "<html><body><p>rendered marker</p></body></html>", ContentArtifactKind.RenderedDom);

        var writer = new PageContentExportWriter(stack.Provider, store, new AngleSharpDomParser());
        await using var output = new MemoryStream();
        await writer.WriteAsync(output, auditId, PageContentFormat.PlainText);

        var bundle = await ReadBundleAsync(output);
        var page = bundle.Values.First(v => !v.StartsWith("{", StringComparison.Ordinal));
        Assert.Contains("rendered marker", page);
        Assert.DoesNotContain("raw body marker", page);

        using var manifest = JsonDocument.Parse(bundle["manifest.json"]);
        var entry = Assert.Single(manifest.RootElement.GetProperty("pages").EnumerateArray());
        Assert.Equal("RenderedDom", entry.GetProperty("artifactKind").GetString());
    }

    /// <summary>An audit with no stored artifacts yields an empty bundle, never a failure.</summary>
    [Fact]
    public async Task NoArtifacts_Yields_Empty_Bundle()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var store = new CompressedContentStore(_artifactRoot, stack.Provider);

        var writer = new PageContentExportWriter(stack.Provider, store, new AngleSharpDomParser());
        await using var output = new MemoryStream();
        var pages = await writer.WriteAsync(output, auditId, PageContentFormat.Markdown);

        Assert.Equal(0, pages);
        var bundle = await ReadBundleAsync(output);
        using var manifest = JsonDocument.Parse(Assert.Single(bundle.Values));
        Assert.Equal(0, manifest.RootElement.GetProperty("pageCount").GetInt32());
    }
}

/// <summary>
/// HTML → Markdown converter unit proofs over the real tolerant parser
/// (no database): the common content vocabulary round-trips to Markdown.
/// </summary>
public sealed class HtmlMarkdownConverterTests
{
    private static string Convert(string html)
        => HtmlMarkdownConverter.Convert(new AngleSharpDomParser().Parse(html, "https://example.com/"));

    /// <summary>Headings, emphasis, links, and images convert inline.</summary>
    [Fact]
    public void Converts_Headings_Emphasis_Links_Images()
    {
        var markdown = Convert("<html><body><h2>Section</h2><p><em>soft</em> and <b>bold</b> <a href=\"/x\">link</a> <img src=\"/i.png\" alt=\"alt\"/></p></body></html>");
        Assert.Contains("## Section", markdown);
        Assert.Contains("*soft*", markdown);
        Assert.Contains("**bold**", markdown);
        Assert.Contains("[link](/x)", markdown);
        Assert.Contains("![alt](/i.png)", markdown);
    }

    /// <summary>Ordered lists number their items; nested lists indent.</summary>
    [Fact]
    public void Converts_Ordered_And_Nested_Lists()
    {
        var markdown = Convert("<html><body><ol><li>one</li><li>two<ol><li>child</li></ol></li></ol></body></html>");
        Assert.Contains("1. one", markdown);
        Assert.Contains("2. two", markdown);
        Assert.Contains("  1. child", markdown);
    }

    /// <summary>Script, style, and head content never enter the Markdown.</summary>
    [Fact]
    public void Drops_NonContent_Elements()
    {
        var markdown = Convert("<html><head><title>t</title><script>evil()</script></head><body><p>keep</p><style>x{}</style></body></html>");
        Assert.Contains("keep", markdown);
        Assert.DoesNotContain("evil", markdown);
        Assert.DoesNotContain("x{}", markdown);
    }

    /// <summary>Malformed markup still converts (tolerant parser contract).</summary>
    [Fact]
    public void Tolerates_Malformed_Markup()
    {
        var markdown = Convert("<html><body><p>unclosed <strong>bold<div>block</div></body>");
        Assert.Contains("unclosed", markdown);
        Assert.Contains("block", markdown);
    }
}