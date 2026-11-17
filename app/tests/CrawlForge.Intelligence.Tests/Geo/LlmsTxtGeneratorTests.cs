using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.Tests.Geo.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Geo;

/// <summary>
/// llms.txt generator (§13.3): ranking contract, directory grouping, bounded
/// full variant, LOCAL files only (E9 — nothing published or submitted).
/// </summary>
public sealed class LlmsTxtGeneratorTests
{
    private static LlmsTxtGenerator CreateGenerator(GeoRealStack stack) =>
        new(stack.Provider, stack.ContentStore, new TestDomParser());

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("word", count));

    private static async Task SeedPagesAsync(GeoRealStack stack)
    {
        var rich = await stack.AddUrlAsync("https://example.test/docs/rich");
        await stack.StoreBodyAsync(rich.Id,
            $"<html><head><title>Rich Guide</title><meta name=\"description\" content=\"The complete guide.\"></head>" +
            $"<body><p>{Words(500)}</p></body></html>");

        var sparse = await stack.AddUrlAsync("https://example.test/docs/sparse");
        await stack.StoreBodyAsync(sparse.Id, "<html><body><p>tiny</p></body></html>");

        var blog = await stack.AddUrlAsync("https://example.test/blog/post");
        await stack.StoreBodyAsync(blog.Id,
            $"<html><head><title>Blog Post</title></head><body><p>{Words(100)}</p></body></html>");

        // Error-finding page must be excluded.
        var errored = await stack.AddUrlAsync("https://example.test/docs/errored");
        await stack.StoreBodyAsync(errored.Id,
            $"<html><head><title>Errored</title></head><body><p>{Words(400)}</p></body></html>");
        await stack.AddFindingAsync("some.error-rule", AuditCategory.Crawl, FindingSeverity.Error, errored.Id);

        // Non-indexable page must be excluded.
        var hidden = await stack.AddUrlAsync("https://example.test/docs/hidden", isIndexable: false);
        await stack.StoreBodyAsync(hidden.Id,
            $"<html><head><title>Hidden</title></head><body><p>{Words(400)}</p></body></html>");
    }

    [Fact]
    public async Task Generate_RanksByQuality_GroupsByDirectory_ExcludesErrorAndNonIndexable()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedPagesAsync(stack);
        var output = Path.Combine(stack.RootDirectory, "llms-out");
        var generator = CreateGenerator(stack);

        var result = await generator.GenerateAsync(new LlmsGenerationRequest(stack.AuditId, output));

        Assert.Equal(2, result.WrittenFilePaths.Count);
        var content = await File.ReadAllTextAsync(Path.Combine(output, "llms.txt"));

        Assert.StartsWith("# example.test", content, StringComparison.Ordinal);
        Assert.Contains("> The complete guide.", content, StringComparison.Ordinal);
        Assert.Contains("## docs", content, StringComparison.Ordinal);
        Assert.Contains("## blog", content, StringComparison.Ordinal);
        Assert.Contains("[Rich Guide](https://example.test/docs/rich)", content, StringComparison.Ordinal);
        Assert.Contains("[Blog Post](https://example.test/blog/post)", content, StringComparison.Ordinal);

        // Exclusions.
        Assert.DoesNotContain("docs/errored", content, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/hidden", content, StringComparison.Ordinal);

        // Ranking: the high-quality page precedes the sparse page in its group.
        Assert.True(
            content.IndexOf("docs/rich", StringComparison.Ordinal) <
            content.IndexOf("docs/sparse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullVariant_AddsBoundedExcerpts()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedPagesAsync(stack);
        var output = Path.Combine(stack.RootDirectory, "llms-out");
        var generator = CreateGenerator(stack);

        await generator.GenerateAsync(new LlmsGenerationRequest(stack.AuditId, output));

        var full = await File.ReadAllTextAsync(Path.Combine(output, "llms-full.txt"));
        Assert.Contains("[Rich Guide](https://example.test/docs/rich)", full, StringComparison.Ordinal);

        // Excerpt lines are indented and bounded at 300 characters.
        var excerptLines = full.Split('\n')
            .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Trim().Length > 0)
            .ToArray();
        Assert.NotEmpty(excerptLines);
        Assert.All(excerptLines, l => Assert.True(l.Trim().Length <= LlmsTxtGenerator.ExcerptLength));
    }

    [Fact]
    public async Task IncludeFullVersionFalse_WritesOnlyStandardFile()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedPagesAsync(stack);
        var output = Path.Combine(stack.RootDirectory, "llms-out");
        var generator = CreateGenerator(stack);

        var result = await generator.GenerateAsync(
            new LlmsGenerationRequest(stack.AuditId, output, IncludeFullVersion: false));

        var path = Assert.Single(result.WrittenFilePaths);
        Assert.EndsWith("llms.txt", path, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "llms-full.txt")));
    }

    [Fact]
    public async Task EmptyAudit_WritesValidSkeleton()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        var output = Path.Combine(stack.RootDirectory, "llms-out");
        var generator = CreateGenerator(stack);

        var result = await generator.GenerateAsync(new LlmsGenerationRequest(stack.AuditId, output));

        Assert.Equal(2, result.WrittenFilePaths.Count);
        var content = await File.ReadAllTextAsync(Path.Combine(output, "llms.txt"));
        Assert.StartsWith("# ", content, StringComparison.Ordinal);
        Assert.Contains("> ", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputIsLocalOnly_NothingOutsideRequestedDirectory()
    {
        await using var stack = await GeoRealStack.CreateAsync();
        await SeedPagesAsync(stack);
        var output = Path.Combine(stack.RootDirectory, "llms-out");
        var filesBefore = StableFiles(stack.RootDirectory);
        var generator = CreateGenerator(stack);

        await generator.GenerateAsync(new LlmsGenerationRequest(stack.AuditId, output));

        var filesAfter = StableFiles(stack.RootDirectory);
        var created = filesAfter.Except(filesBefore).ToArray();
        Assert.Equal(2, created.Length);
        Assert.All(created, f => Assert.StartsWith(output, f, StringComparison.Ordinal));
    }

    // SQLite WAL/SHM sidecars are transient connection-pool artifacts, not
    // product output; the local-only invariant concerns durable files.
    private static string[] StableFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
}
